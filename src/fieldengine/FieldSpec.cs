using System.Globalization;
using System.Text.Json;

namespace FieldEngine;

/// <summary>Parsed, validated field-compile spec (`obc-field/0.1`). All units mm,
/// declared never inferred; every physical value is caller-supplied. Bands are in
/// the mesh's own coordinate frame — the engine does not recentre geometry,
/// because that would silently change what the band boundaries mean.</summary>
public sealed record FieldSpec
{
    public required string StrMeshPath { get; init; }
    public required float FVoxelSizeMm { get; init; }
    public required string StrFieldType { get; init; }        // "axis_bands" | "vdb_regions"
    public string StrAxis { get; init; } = "z";               // axis_bands only
    public IReadOnlyList<Band> Bands { get; init; } = Array.Empty<Band>();
    public IReadOnlyList<VdbRegion> VdbRegions { get; init; } = Array.Empty<VdbRegion>();
    public Band? ORemainder { get; init; }                    // vdb_regions: part volume no region claims
    public required JsonElement OPrinter { get; init; }       // passed through to the manifest

    public sealed record Band(int NFilament, float? FUpToMm, IReadOnlyDictionary<string, string> Overrides);
    public sealed record VdbRegion(string StrVdbPath, int NFilament, IReadOnlyDictionary<string, string> Overrides);

    public static FieldSpec OParse(string strSpecPath)
    {
        if (!File.Exists(strSpecPath))
            throw new FileNotFoundException($"Spec not found: {strSpecPath}");
        string strDir = Path.GetDirectoryName(Path.GetFullPath(strSpecPath))!;
        using JsonDocument oDoc = JsonDocument.Parse(File.ReadAllText(strSpecPath));
        JsonElement oRoot = oDoc.RootElement;

        // geometry
        if (!oRoot.TryGetProperty("geometry", out JsonElement oGeo) || oGeo.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Spec needs a \"geometry\" object.");
        string strUnits = StrReq(oGeo, "units", "geometry");
        if (strUnits != "mm")
            throw new InvalidDataException(
                $"geometry.units is '{strUnits}' — v0.1 accepts only \"mm\", declared explicitly. " +
                "Convert the mesh; silent unit inference and conversion are forbidden.");
        string strMeshRel = StrReq(oGeo, "mesh", "geometry");
        string strMesh = Path.IsPathRooted(strMeshRel) ? strMeshRel : Path.Combine(strDir, strMeshRel);
        if (!File.Exists(strMesh))
            throw new FileNotFoundException($"Mesh not found: {strMesh}");

        // voxel size — explicit input, never defaulted (ecosystem invariant)
        if (!oRoot.TryGetProperty("voxel_size_mm", out JsonElement oVox)
            || oVox.ValueKind != JsonValueKind.String
            || !float.TryParse(oVox.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float fVoxel)
            || fVoxel <= 0)
            throw new InvalidDataException(
                "Spec needs \"voxel_size_mm\" as a positive decimal string — it is an explicit " +
                "input to every run and is never defaulted.");

        // field
        if (!oRoot.TryGetProperty("field", out JsonElement oField) || oField.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Spec needs a \"field\" object.");
        string strType = StrReq(oField, "type", "field");
        if (strType == "vdb_regions")
            return OParseVdbRegions(oRoot, oField, strMesh, fVoxel, strDir);
        if (strType != "axis_bands")
            throw new InvalidDataException(
                $"field.type '{strType}' unknown. Implemented: \"axis_bands\", \"vdb_regions\".");
        string strAxis = StrReq(oField, "axis", "field");
        if (strAxis is not ("x" or "y" or "z"))
            throw new InvalidDataException("field.axis must be \"x\", \"y\" or \"z\".");

        if (!oField.TryGetProperty("bands", out JsonElement oBands)
            || oBands.ValueKind != JsonValueKind.Array || oBands.GetArrayLength() < 2)
            throw new InvalidDataException(
                "field.bands needs at least two bands — one band is not a field.");

        var aBands = new List<Band>();
        float? fPrev = null;
        int nIndex = 0;
        foreach (JsonElement oBand in oBands.EnumerateArray())
        {
            nIndex++;
            if (!oBand.TryGetProperty("filament", out JsonElement oFil)
                || oFil.ValueKind != JsonValueKind.Number || oFil.GetInt32() < 1)
                throw new InvalidDataException($"Band {nIndex}: needs \"filament\" >= 1.");
            float? fUpTo = null;
            if (oBand.TryGetProperty("up_to_mm", out JsonElement oUp))
            {
                if (oUp.ValueKind != JsonValueKind.String
                    || !float.TryParse(oUp.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                    throw new InvalidDataException($"Band {nIndex}: up_to_mm must be a decimal string.");
                fUpTo = f;
            }
            bool bLast = nIndex == oBands.GetArrayLength();
            if (bLast && fUpTo is not null)
                throw new InvalidDataException("The last band is open-ended: omit up_to_mm on it.");
            if (!bLast && fUpTo is null)
                throw new InvalidDataException($"Band {nIndex}: every band except the last needs up_to_mm.");
            if (fUpTo is float fCur)
            {
                if (fPrev is float fP && fCur <= fP)
                    throw new InvalidDataException(
                        $"Band {nIndex}: up_to_mm must be strictly ascending ({fCur} after {fP}).");
                float fThickness = fCur - (fPrev ?? float.NegativeInfinity);
                if (fPrev is not null && fThickness < 2 * fVoxel)
                    throw new InvalidDataException(
                        $"Band {nIndex}: thickness {fThickness:F3} mm is below the resolution floor " +
                        $"of 2 voxels ({2 * fVoxel:F3} mm). Use a finer voxel size or thicker bands.");
                fPrev = fCur;
            }
            // Per-band process overrides: the P3 process-grading seam.
            aBands.Add(new Band(oFil.GetInt32(), fUpTo, OParseOverrides(oBand, $"Band {nIndex}")));
        }

        if (!oRoot.TryGetProperty("printer", out JsonElement oPrinter) || oPrinter.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(
                "Spec needs a \"printer\" block (passed through to the obc3mf manifest; " +
                "machine facts are never defaulted).");

        return new FieldSpec
        {
            StrMeshPath = strMesh,
            FVoxelSizeMm = fVoxel,
            StrFieldType = "axis_bands",
            StrAxis = strAxis,
            Bands = aBands,
            OPrinter = oPrinter.Clone(),
        };
    }

    /// <summary>`vdb_regions`: each region is an OpenVDB volume (anything PicoGK,
    /// OpenVCAD, or `make-vdb` emits) intersected with the part. Priority order:
    /// earlier regions win where volumes overlap. Part volume claimed by no
    /// region needs an explicit `remainder` — no silent material assignment.</summary>
    private static FieldSpec OParseVdbRegions(
        JsonElement oRoot, JsonElement oField, string strMesh, float fVoxel, string strDir)
    {
        if (!oField.TryGetProperty("regions", out JsonElement oRegions)
            || oRegions.ValueKind != JsonValueKind.Array || oRegions.GetArrayLength() == 0)
            throw new InvalidDataException("field.regions needs at least one { vdb, filament } entry.");

        var aRegions = new List<VdbRegion>();
        int nIndex = 0;
        foreach (JsonElement oRegion in oRegions.EnumerateArray())
        {
            nIndex++;
            string strVdbRel = StrReq(oRegion, "vdb", $"region {nIndex}");
            string strVdb = Path.IsPathRooted(strVdbRel) ? strVdbRel : Path.Combine(strDir, strVdbRel);
            if (!File.Exists(strVdb))
                throw new FileNotFoundException($"Region {nIndex}: vdb not found: {strVdb}");
            if (!oRegion.TryGetProperty("filament", out JsonElement oFil)
                || oFil.ValueKind != JsonValueKind.Number || oFil.GetInt32() < 1)
                throw new InvalidDataException($"Region {nIndex}: needs \"filament\" >= 1.");
            aRegions.Add(new VdbRegion(strVdb, oFil.GetInt32(), OParseOverrides(oRegion, $"region {nIndex}")));
        }

        Band? oRemainder = null;
        if (oField.TryGetProperty("remainder", out JsonElement oRem))
        {
            if (oRem.ValueKind != JsonValueKind.Object
                || !oRem.TryGetProperty("filament", out JsonElement oRf)
                || oRf.ValueKind != JsonValueKind.Number || oRf.GetInt32() < 1)
                throw new InvalidDataException("field.remainder needs { \"filament\": N }.");
            oRemainder = new Band(oRf.GetInt32(), null, OParseOverrides(oRem, "remainder"));
        }

        if (!oRoot.TryGetProperty("printer", out JsonElement oPrinter) || oPrinter.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(
                "Spec needs a \"printer\" block (passed through to the obc3mf manifest; " +
                "machine facts are never defaulted).");

        return new FieldSpec
        {
            StrMeshPath = strMesh,
            FVoxelSizeMm = fVoxel,
            StrFieldType = "vdb_regions",
            VdbRegions = aRegions,
            ORemainder = oRemainder,
            OPrinter = oPrinter.Clone(),
        };
    }

    private static IReadOnlyDictionary<string, string> OParseOverrides(JsonElement oOwner, string strWhere)
    {
        var oOverrides = new Dictionary<string, string>(StringComparer.Ordinal);
        if (oOwner.TryGetProperty("overrides", out JsonElement oOv))
        {
            if (oOv.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"{strWhere}: overrides must be an object.");
            foreach (JsonProperty oProp in oOv.EnumerateObject())
            {
                if (oProp.Name is "extruder" or "filament")
                    throw new InvalidDataException(
                        $"{strWhere}: material comes from \"filament\", never an override.");
                oOverrides[oProp.Name] = oProp.Value.GetString()
                    ?? throw new InvalidDataException(
                        $"{strWhere}: override '{oProp.Name}' must be a string value.");
            }
        }
        return oOverrides;
    }

    private static string StrReq(JsonElement o, string strKey, string strWhere)
        => o.TryGetProperty(strKey, out JsonElement e) && e.ValueKind == JsonValueKind.String
            && e.GetString() is string s && s.Length > 0
            ? s : throw new InvalidDataException($"{strWhere}.{strKey} must be a non-empty string.");
}
