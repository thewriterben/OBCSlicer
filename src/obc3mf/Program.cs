using System.Globalization;
using System.Text.Json;
using ObcSlice;

namespace Obc3mf;

/// <summary>
/// obc3mf — compile meshes + filament assignments into a multi-material 3MF
/// project for OrcaSlicer_AS, with a canonical compile record. See
/// docs/specs/MM-3MF.md. Meshes are millimetres, declared by the manifest.
/// </summary>
public static class Program
{
    private const string StrUsage = """
        obc3mf compile --manifest FILE [--allow-open-mesh]

        Manifest: { "output": "part.3mf",
                    "printer": { "nozzle_diameter_mm": "0.4",
                                 "printable_width_mm": 350, "printable_depth_mm": 350,
                                 "printable_height_mm": 350 },
                    "parts": [ { "mesh": "a.stl", "name": "body", "filament": 1,
                                 "material": "pla-basic.material.json",
                                 "overrides": { "nozzle_temperature": "230" } }, ... ] }

        "material" (optional) names an obc-material/0.1 record (obcslice material pin).
        Its hash enters the compile record and any nozzle_temperature override must lie
        inside the record's published print range, else the compile is refused.

        The printer block is required: project emission embeds machine facts and
        this tool never invents physical data.
        Writes the 3MF and <output>.project.json (canonical, obc-project/0.1).
        Exit codes: 0 ok · 2 invalid manifest/mesh (open meshes refuse without --allow-open-mesh).
        """;

    public static int Main(string[] args)
    {
        try
        {
            return MainInner(args);
        }
        catch (Exception e) when (e is InvalidDataException or FileNotFoundException or ArgumentException)
        {
            Console.Error.WriteLine($"obc3mf: {e.Message}");
            return 2;
        }
    }

    private static int MainInner(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            Console.WriteLine(StrUsage);
            return args.Length == 0 ? 2 : 0;
        }
        if (args[0] != "compile")
            throw new ArgumentException($"Unknown command '{args[0]}'. Only 'compile' exists in v0.1.");

        string? strManifest = null;
        bool bAllowOpen = false;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--manifest":
                    strManifest = i + 1 < args.Length ? args[++i]
                        : throw new ArgumentException("--manifest needs a value.");
                    break;
                case "--allow-open-mesh": bAllowOpen = true; break;
                default: throw new ArgumentException($"Unknown argument '{args[i]}'.");
            }
        }
        if (strManifest is null || !File.Exists(strManifest))
            throw new FileNotFoundException($"Manifest not found: {strManifest}");

        string strManifestDir = Path.GetDirectoryName(Path.GetFullPath(strManifest))!;
        using JsonDocument oDoc = JsonDocument.Parse(File.ReadAllText(strManifest));
        JsonElement oRoot = oDoc.RootElement;

        string strOutput = oRoot.TryGetProperty("output", out JsonElement oOut)
            && oOut.GetString() is string s
            ? Path.IsPathRooted(s) ? s : Path.Combine(strManifestDir, s)
            : throw new InvalidDataException("Manifest needs \"output\".");
        if (!oRoot.TryGetProperty("parts", out JsonElement oParts)
            || oParts.ValueKind != JsonValueKind.Array || oParts.GetArrayLength() == 0)
            throw new InvalidDataException("Manifest needs a non-empty \"parts\" array.");

        if (!oRoot.TryGetProperty("printer", out JsonElement oPr) || oPr.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(
                "Manifest needs a \"printer\" block (nozzle_diameter_mm, printable_width_mm, " +
                "printable_depth_mm, printable_height_mm) — machine facts are never defaulted.");
        string StrPr(string strKey) => oPr.TryGetProperty(strKey, out JsonElement o)
            && o.ValueKind == JsonValueKind.String && o.GetString() is string s && s.Length > 0
            ? s : throw new InvalidDataException($"printer.{strKey} must be a non-empty string.");
        int NPr(string strKey) => oPr.TryGetProperty(strKey, out JsonElement o)
            && o.ValueKind == JsonValueKind.Number && o.GetInt32() > 0
            ? o.GetInt32() : throw new InvalidDataException($"printer.{strKey} must be a positive integer (mm).");
        var oPrinter = new ThreeMfWriter.PrinterSpec(
            StrPr("nozzle_diameter_mm"),
            NPr("printable_width_mm"), NPr("printable_depth_mm"), NPr("printable_height_mm"));

        // assembly: all parts become volumes of ONE object (required when parts
        // overlap in XY, e.g. stacked field bands — separate objects refuse to slice).
        bool bAssembly = oRoot.TryGetProperty("assembly", out JsonElement oAsm)
            && oAsm.ValueKind == JsonValueKind.True;

        var aDegradations = new List<Degradation>();
        var aParts = new List<ThreeMfWriter.Part>();
        var aPartRecords = new List<object?>();

        foreach (JsonElement oPart in oParts.EnumerateArray())
        {
            string strMeshRel = oPart.TryGetProperty("mesh", out JsonElement oMesh)
                && oMesh.GetString() is string m ? m
                : throw new InvalidDataException("Every part needs \"mesh\".");
            string strMesh = Path.IsPathRooted(strMeshRel)
                ? strMeshRel : Path.Combine(strManifestDir, strMeshRel);
            if (!File.Exists(strMesh))
                throw new FileNotFoundException($"Mesh not found: {strMesh}");

            string strName = oPart.TryGetProperty("name", out JsonElement oName)
                && oName.GetString() is string n ? n : Path.GetFileNameWithoutExtension(strMesh);
            int nFilament = oPart.TryGetProperty("filament", out JsonElement oFil)
                && oFil.ValueKind == JsonValueKind.Number ? oFil.GetInt32()
                : throw new InvalidDataException($"Part '{strName}' needs an integer \"filament\".");
            if (nFilament < 1)
                throw new InvalidDataException($"Part '{strName}': filament slots are 1-based.");

            var oOverrides = new Dictionary<string, string>(StringComparer.Ordinal);
            if (oPart.TryGetProperty("overrides", out JsonElement oOv)
                && oOv.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty oProp in oOv.EnumerateObject())
                    oOverrides[oProp.Name] = oProp.Value.ValueKind == JsonValueKind.String
                        && oProp.Value.GetString() is string strV ? strV
                        : throw new InvalidDataException(
                            $"Part '{strName}': override '{oProp.Name}' must be a string.");
            if (oOverrides.ContainsKey("extruder"))
                throw new InvalidDataException(
                    $"Part '{strName}': set \"filament\", not an 'extruder' override.");

            object? oMaterialRecord = null;
            if (oPart.TryGetProperty("material", out JsonElement oMatEl))
            {
                string strMatRel = oMatEl.ValueKind == JsonValueKind.String && oMatEl.GetString() is string strM
                    && strM.Length > 0 ? strM
                    : throw new InvalidDataException($"Part '{strName}': \"material\" must be a path string.");
                string strMat = Path.IsPathRooted(strMatRel) ? strMatRel : Path.Combine(strManifestDir, strMatRel);
                if (!File.Exists(strMat))
                    throw new FileNotFoundException($"Part '{strName}': material record not found: {strMat}");
                MaterialPin.Summary oMatSum = MaterialPin.Load(strMat);
                foreach (string strKey in new[] { "nozzle_temperature", "nozzle_temperature_initial_layer" })
                {
                    if (!oOverrides.TryGetValue(strKey, out string? strT)) continue;
                    if (!int.TryParse(strT, NumberStyles.Integer, CultureInfo.InvariantCulture, out int nT))
                        throw new InvalidDataException($"Part '{strName}': {strKey} '{strT}' is not an integer.");
                    if (oMatSum.NMinNozzleC is null || oMatSum.NMaxNozzleC is null)
                    {
                        aDegradations.Add(new Degradation("material_temperature_unverified",
                            $"{strName}: {strKey}={nT} set, but {oMatSum.StrName} publishes no print range in OFD {oMatSum.StrDatasetVersion}."));
                        continue;
                    }
                    if (nT < oMatSum.NMinNozzleC || nT > oMatSum.NMaxNozzleC)
                        throw new InvalidDataException(
                            $"Part '{strName}': {strKey}={nT} is outside {oMatSum.StrName}'s published range " +
                            $"{oMatSum.NMinNozzleC}-{oMatSum.NMaxNozzleC} C (OFD {oMatSum.StrDatasetVersion}). " +
                            "Change the override or pin a different material; the compiler will not guess.");
                }
                oMaterialRecord = new Dictionary<string, object?>
                {
                    ["record"] = Path.GetFileName(strMat),
                    ["record_sha256"] = oMatSum.StrRecordSha256,
                    ["filament_id"] = oMatSum.StrFilamentId,
                    ["filament_name"] = oMatSum.StrName,
                    ["dataset_version"] = oMatSum.StrDatasetVersion,
                };
            }

            IndexedMesh oIndexed = StlReader.Read(strMesh);
            int nOpen = oIndexed.NOpenEdges();
            if (nOpen > 0)
            {
                if (!bAllowOpen)
                    throw new InvalidDataException(
                        $"Part '{strName}': mesh is not closed ({nOpen} open/non-manifold edges). " +
                        "Repair it, or pass --allow-open-mesh to proceed with a recorded degradation.");
                aDegradations.Add(new Degradation("open_mesh",
                    $"{strName}: {nOpen} open/non-manifold edges; sliced anyway by request."));
            }

            aParts.Add(new ThreeMfWriter.Part(strName, oIndexed, nFilament, oOverrides));
            aPartRecords.Add(new Dictionary<string, object?>
            {
                ["name"] = strName,
                ["mesh"] = Path.GetFileName(strMesh),
                ["mesh_sha256"] = SliceRecord.StrHashFile(strMesh),
                ["filament"] = nFilament,
                ["triangles"] = oIndexed.Triangles.Count,
                ["overrides"] = oOverrides.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                ["material"] = oMaterialRecord,
            });
        }

        ThreeMfWriter.Write(strOutput, aParts, oPrinter, bAssembly);

        var oRecord = new Dictionary<string, object?>
        {
            ["record"] = "obc-project/0.2",
            ["structure"] = bAssembly ? "orca-project-assembly" : "orca-project",
            ["printer"] = new Dictionary<string, object?>
            {
                ["nozzle_diameter_mm"] = oPrinter.StrNozzleDiameterMm,
                ["printable_width_mm"] = oPrinter.NPrintableWidthMm,
                ["printable_depth_mm"] = oPrinter.NPrintableDepthMm,
                ["printable_height_mm"] = oPrinter.NPrintableHeightMm,
            },
            ["created_utc"] = DateTime.UtcNow.ToString(
                "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["units"] = "mm",
            ["parts"] = aPartRecords,
            ["output"] = new Dictionary<string, object?>
            {
                ["name"] = Path.GetFileName(strOutput),
                ["sha256"] = SliceRecord.StrHashFile(strOutput),
                ["media_type"] = "model/3mf",
            },
            ["tool"] = new Dictionary<string, object?>
            {
                ["name"] = "obc3mf", ["version"] = "0.1.0",
            },
            ["degradations"] = aDegradations.Select(d => (object?)new Dictionary<string, object?>
            {
                ["code"] = d.StrCode, ["detail"] = d.StrDetail,
            }).ToList(),
        };
        string strRecordPath = strOutput + ".project.json";
        File.WriteAllBytes(strRecordPath, CanonicalJson.Serialize(oRecord));

        Console.WriteLine($"3mf: {strOutput}");
        Console.WriteLine($"compile record: {strRecordPath}");
        foreach (Degradation oD in aDegradations)
            Console.WriteLine($"degradation: {oD.StrCode} — {oD.StrDetail}");
        return 0;
    }
}
