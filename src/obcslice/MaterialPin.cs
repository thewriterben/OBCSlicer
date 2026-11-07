using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace ObcSlice;

/// <summary>
/// `obc-material/0.1`: a pinned, manifest-verified material record taken from the
/// Open Filament Database (OFD). The record is the only way material facts enter
/// this slicer: every number is the source's own JSON text (never re-parsed into
/// a float), the bytes are checked against OFD's `manifest.json` sha256 for the
/// stated dataset version, and fields OFD does not carry (shrinkage, mechanical
/// data) are named as absent rather than defaulted. See docs/specs/MATERIAL.md.
///
/// Unit suffixes follow the OFD filament schema descriptions
/// (api/v1/schemas/filament_schema.json, dataset 2026.11.04): density g/cm3,
/// diameter tolerance mm, temperatures degC, volumetric speed mm3/s, fan %, dry time h.
/// </summary>
public static class MaterialPin
{
    public const string StrSchema = "obc-material/0.1";
    public const string StrDatabase = "open-filament-database";
    public const string StrBaseUrl = "https://api.openfilamentdatabase.org/";
    public const string StrFilamentSchemaPath = "api/v1/schemas/filament_schema.json";

    private static readonly string[] AstrRequired = { "id", "name", "density", "diameter_tolerance" };

    // (OFD key, record key with unit). Order is irrelevant; canonical JSON sorts.
    private static readonly (string StrOfd, string StrOut)[] AoLifted =
    {
        ("density", "density_g_cm3"),
        ("diameter_tolerance", "diameter_tolerance_mm"),
        ("min_print_temperature", "min_print_temperature_c"),
        ("max_print_temperature", "max_print_temperature_c"),
        ("preheat_temperature", "preheat_temperature_c"),
        ("min_bed_temperature", "min_bed_temperature_c"),
        ("max_bed_temperature", "max_bed_temperature_c"),
        ("min_chamber_temperature", "min_chamber_temperature_c"),
        ("max_chamber_temperature", "max_chamber_temperature_c"),
        ("chamber_temperature", "chamber_temperature_c"),
        ("min_print_speed", "min_print_speed_mm_s"),
        ("max_print_speed", "max_print_speed_mm_s"),
        ("max_volumetric_speed", "max_volumetric_speed_mm3_s"),
        ("min_fan_speed", "min_fan_speed_pct"),
        ("max_fan_speed", "max_fan_speed_pct"),
        ("dry_temperature", "dry_temperature_c"),
        ("max_dry_temperature", "max_dry_temperature_c"),
        ("dry_time", "dry_time_h"),
        ("min_nozzle_diameter", "min_nozzle_diameter_mm"),
        ("shore_hardness_a", "shore_hardness_a"),
        ("shore_hardness_d", "shore_hardness_d"),
    };

    /// <summary>What OFD does not publish, so this record never claims it
    /// (OpenDesignCore ADR-0013: OFD must not be cited for these).</summary>
    private static readonly string[] AstrNotInSource =
        { "shrinkage", "tensile_strength", "modulus", "glass_transition", "composition" };

    public static string StrManifestPath(string strBrand, string strMaterial, string strFilament)
        => $"api/v1/brands/{strBrand}/materials/{strMaterial}/filaments/{strFilament}/index.json";

    /// <summary>Each id becomes one URL path segment, so it may not carry a separator,
    /// a parent step, or a query/fragment marker. Throws ArgumentException.</summary>
    public static void ValidateSegments(params string[] astr)
    {
        foreach (string s in astr)
            if (s.Length == 0 || s.IndexOfAny(new[] { '/', '\\', '?', '#' }) >= 0
                || s.Contains("..", StringComparison.Ordinal))
                throw new ArgumentException($"Invalid path segment '{s}'.");
    }

    /// <summary>True when the manifest lists <paramref name="strPath"/>. Lets the fetch
    /// path refuse an unknown filament before requesting it.</summary>
    public static bool BInManifest(byte[] abManifest, string strPath)
    {
        try
        {
            using JsonDocument oMan = JsonDocument.Parse(abManifest);
            if (!oMan.RootElement.TryGetProperty("artifacts", out JsonElement oArts)
                || oArts.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("manifest has no \"artifacts\" array.");
            foreach (JsonElement oArt in oArts.EnumerateArray())
                if (oArt.ValueKind == JsonValueKind.Object && oArt.TryGetProperty("path", out JsonElement oP)
                    && oP.ValueKind == JsonValueKind.String && oP.GetString() == strPath)
                    return true;
            return false;
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"manifest is not valid JSON: {e.Message}");
        }
    }

    /// <summary>Build the canonical record. Throws InvalidDataException on any
    /// refusal: not in manifest, hash mismatch, size mismatch, missing required
    /// field, non-numeric value where the schema says number, identity mismatch.</summary>
    public static byte[] Build(
        string strBrand, string strMaterial, string strFilament,
        byte[] abFilament, byte[] abManifest, DateTime oRetrievedUtc)
    {
        ValidateSegments(strBrand, strMaterial, strFilament);
        string strPath = StrManifestPath(strBrand, strMaterial, strFilament);

        // --- manifest: dataset identity and the expected hash ---------------
        using JsonDocument oMan = JsonDocument.Parse(abManifest);
        JsonElement oManRoot = oMan.RootElement;
        string strDataset = StrField(oManRoot, "dataset_version", "manifest");
        string strGenerated = StrField(oManRoot, "generated_at", "manifest");
        if (!oManRoot.TryGetProperty("artifacts", out JsonElement oArts) || oArts.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("manifest has no \"artifacts\" array.");

        string? strExpected = null;
        long nExpectedSize = -1;
        foreach (JsonElement oArt in oArts.EnumerateArray())
        {
            if (oArt.TryGetProperty("path", out JsonElement oP) && oP.GetString() == strPath)
            {
                strExpected = StrField(oArt, "sha256", "manifest artifact").ToLowerInvariant();
                if (oArt.TryGetProperty("size", out JsonElement oSz) && oSz.ValueKind == JsonValueKind.Number)
                    nExpectedSize = oSz.GetInt64();
                break;
            }
        }
        if (strExpected is null)
            throw new InvalidDataException(
                $"{strPath} is not in OFD manifest {strDataset}: brand/material/filament does not exist in that dataset.");

        string strActual = Convert.ToHexString(SHA256.HashData(abFilament)).ToLowerInvariant();
        if (strActual != strExpected)
            throw new InvalidDataException(
                $"filament bytes do not match OFD manifest {strDataset}: manifest {strExpected}, got {strActual}. " +
                "Re-fetch both from the same dataset version; never pin unverified bytes.");
        if (nExpectedSize >= 0 && nExpectedSize != abFilament.LongLength)
            throw new InvalidDataException(
                $"filament size {abFilament.LongLength} differs from manifest size {nExpectedSize}.");

        // --- filament record ------------------------------------------------
        using JsonDocument oFil = JsonDocument.Parse(abFilament);
        JsonElement oRoot = oFil.RootElement;
        if (oRoot.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("filament record is not a JSON object.");
        foreach (string strKey in AstrRequired)
            if (!oRoot.TryGetProperty(strKey, out _))
                throw new InvalidDataException(
                    $"filament record lacks required field \"{strKey}\" (OFD filament schema).");

        string strId = StrField(oRoot, "id", "filament");
        string strName = StrField(oRoot, "name", "filament");
        if (oRoot.TryGetProperty("slug", out JsonElement oSlug) && oSlug.GetString() is string slug
            && slug != strFilament)
            throw new InvalidDataException($"filament slug \"{slug}\" is not the requested \"{strFilament}\".");
        if (oRoot.TryGetProperty("material", out JsonElement oMat) && oMat.GetString() is string mat
            && mat != strMaterial)
            throw new InvalidDataException($"filament material \"{mat}\" is not the requested \"{strMaterial}\".");

        var oProps = new Dictionary<string, object?>(StringComparer.Ordinal);
        var aAbsent = new List<object?>();
        foreach ((string strOfd, string strOut) in AoLifted)
        {
            if (!oRoot.TryGetProperty(strOfd, out JsonElement oV) || oV.ValueKind == JsonValueKind.Null)
            {
                aAbsent.Add(strOut);
                continue;
            }
            if (oV.ValueKind != JsonValueKind.Number)
                throw new InvalidDataException(
                    $"filament field \"{strOfd}\" is {oV.ValueKind}, schema says number; refusing to coerce.");
            oProps[strOut] = oV.GetRawText(); // the source's own digits, verbatim
        }

        var oSlicers = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (oRoot.TryGetProperty("slicer_settings", out JsonElement oSs) && oSs.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty oSl in oSs.EnumerateObject())
            {
                if (oSl.Value.ValueKind != JsonValueKind.Object) continue;
                var oEntry = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (string k in new[] { "id", "generic_id", "profile_name" })
                    if (oSl.Value.TryGetProperty(k, out JsonElement oE) && oE.ValueKind == JsonValueKind.String)
                        oEntry[k] = oE.GetString();
                oSlicers[oSl.Name] = oEntry;
            }

        var oLinks = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (string k in new[] { "data_sheet_url", "safety_sheet_url" })
            if (oRoot.TryGetProperty(k, out JsonElement oL) && oL.ValueKind == JsonValueKind.String)
                oLinks[k] = oL.GetString();

        bool bDiscontinued = oRoot.TryGetProperty("discontinued", out JsonElement oD)
            && oD.ValueKind == JsonValueKind.True;

        var oRecord = new Dictionary<string, object?>
        {
            ["record"] = StrSchema,
            ["source"] = new Dictionary<string, object?>
            {
                ["database"] = StrDatabase,
                ["dataset_version"] = strDataset,
                ["manifest_generated_at"] = strGenerated,
                ["manifest_url"] = StrBaseUrl + "manifest.json",
                ["path"] = strPath,
                ["url"] = StrBaseUrl + strPath,
                ["sha256"] = strActual,
                ["size"] = abFilament.LongLength,
                ["schema"] = StrBaseUrl + StrFilamentSchemaPath,
                ["retrieved_utc"] = oRetrievedUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            },
            ["filament"] = new Dictionary<string, object?>
            {
                ["id"] = strId,
                ["name"] = strName,
                ["brand"] = strBrand,
                ["material"] = strMaterial,
                ["slug"] = strFilament,
                ["discontinued"] = bDiscontinued,
            },
            ["properties"] = oProps,
            ["absent_in_source"] = aAbsent,
            ["not_in_source"] = AstrNotInSource.Select(s => (object?)s).ToList(),
            ["slicer_profiles"] = oSlicers,
            ["links"] = oLinks,
            ["tool"] = new Dictionary<string, object?> { ["name"] = "obcslice", ["version"] = "0.1.0" },
        };
        return CanonicalJson.Serialize(oRecord);
    }

    /// <summary>What a consumer (obc3mf) needs from a pinned record: identity and
    /// the nozzle range, if OFD published one. Throws on a malformed record.</summary>
    public sealed record Summary(
        string StrRecordSha256, string StrFilamentId, string StrName, string StrDatasetVersion,
        int? NMinNozzleC, int? NMaxNozzleC);

    public static Summary Load(string strPath)
    {
        byte[] ab = File.ReadAllBytes(strPath);
        using JsonDocument oDoc = JsonDocument.Parse(ab);
        JsonElement oRoot = oDoc.RootElement;
        if (!oRoot.TryGetProperty("record", out JsonElement oR) || oR.GetString() != StrSchema)
            throw new InvalidDataException($"{strPath}: not an {StrSchema} record.");
        JsonElement oFil = oRoot.GetProperty("filament");
        JsonElement oSrc = oRoot.GetProperty("source");
        JsonElement oProps = oRoot.GetProperty("properties");
        // Build keeps the source's own number text, so 230, 230.0 and 2.3e2 can all appear.
        int? NTemp(string k)
        {
            if (!oProps.TryGetProperty(k, out JsonElement o) || o.ValueKind != JsonValueKind.String
                || o.GetString() is not string s)
                return null;
            if (!decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d))
                throw new InvalidDataException($"{strPath}: {k} '{s}' is not a number.");
            if (d != decimal.Truncate(d) || d < int.MinValue || d > int.MaxValue)
                throw new InvalidDataException($"{strPath}: {k} '{s}' is not a whole degree.");
            return (int)d;
        }
        return new Summary(
            Convert.ToHexString(SHA256.HashData(ab)).ToLowerInvariant(),
            StrField(oFil, "id", "material record"), StrField(oFil, "name", "material record"),
            StrField(oSrc, "dataset_version", "material record"),
            NTemp("min_print_temperature_c"), NTemp("max_print_temperature_c"));
    }

    private static string StrField(JsonElement o, string strKey, string strWhat)
        => o.TryGetProperty(strKey, out JsonElement v) && v.ValueKind == JsonValueKind.String
           && v.GetString() is string s && s.Length > 0
            ? s : throw new InvalidDataException($"{strWhat} lacks string field \"{strKey}\".");
}
