using System.Security.Cryptography;
using System.Text.Json;
using ObcSlice;

namespace ObcSlicerMcp;

/// <summary>The two tools of v0.1. `slice` executes locally (it writes only into
/// its own output directory). `stage_for_studio` copies hash-named artifacts into
/// AdvancedStudio's bounded staging directory — staging is not proposing: the
/// upload/print proposals and their human approval stay in AdvancedStudio
/// (ADR-0004). A propose passthrough is deliberately absent until that API's
/// source can be read rather than inferred.</summary>
public static class Tools
{
    public static List<object?> ToolDefinitions() => new()
    {
        new Dictionary<string, object?>
        {
            ["name"] = "slice",
            ["description"] =
                "Slice a model headlessly via OrcaSlicer_AS and write a canonical slice record " +
                "(obc-slice/0.1) next to the G-code. Verifies an ODC design sidecar against the " +
                "model hash before slicing and refuses on mismatch. Degradations are always " +
                "reported, never silent.",
            ["inputSchema"] = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["model"] = OStr("Path to the model file (stl/3mf/step/obj)."),
                    ["machine"] = OStr("Path to the machine profile JSON (flattened preferred)."),
                    ["process"] = OStr("Path to the process profile JSON (flattened preferred)."),
                    ["filaments"] = new Dictionary<string, object?>
                    {
                        ["type"] = "array", ["items"] = OStr("Filament profile JSON path."),
                        ["description"] = "Filament profiles in slot order.",
                    },
                    ["outdir"] = OStr("Output directory for G-code and the slice record."),
                    ["slicer"] = OStr("Path to the orca-slicer binary (default: env OBCSLICE_ORCA)."),
                    ["design_sidecar"] = OStr("Path to the ODC provenance sidecar for the model."),
                    ["design_run_id"] = OStr("ODC ledger run id, when known."),
                    ["slicer_commit"] = OStr("Git commit of the slicer fork, when known."),
                    ["plate"] = new Dictionary<string, object?>
                    {
                        ["type"] = "integer", ["description"] = "Plate to slice; 0 = all (default).",
                    },
                    ["overrides"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = new Dictionary<string, object?> { ["type"] = "string" },
                        ["description"] = "Extra CLI options as key/value, e.g. {\"curr-bed-type\":\"…\"}.",
                    },
                    ["dry_run"] = new Dictionary<string, object?>
                    {
                        ["type"] = "boolean",
                        ["description"] = "Build and record the invocation without executing the slicer.",
                    },
                },
                ["required"] = new List<object?> { "model", "machine", "process", "outdir" },
            },
        },
        new Dictionary<string, object?>
        {
            ["name"] = "stage_for_studio",
            ["description"] =
                "Copy a sliced G-code file and its slice record, hash-named, into AdvancedStudio's " +
                "staging directory. Refuses records carrying slice_failed/dry_run/output_missing " +
                "degradations and refuses G-code whose hash is not attested by the record. Staging " +
                "only — upload and print remain human-approved proposals inside AdvancedStudio.",
            ["inputSchema"] = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["gcode"] = OStr("Path to the plate G-code file."),
                    ["slice_record"] = OStr("Path to the .slice.json record attesting it."),
                    ["staging_dir"] = OStr("AdvancedStudio staging directory (default: env OBCSLICE_STAGING)."),
                },
                ["required"] = new List<object?> { "gcode", "slice_record" },
            },
        },
    };

    private static Dictionary<string, object?> OStr(string strDescription) => new()
    {
        ["type"] = "string", ["description"] = strDescription,
    };

    /// <summary>Dispatch a tools/call. Returns the MCP content text; throws
    /// ToolError for refusals (mapped to isError content by the server).</summary>
    public static string StrCall(string strName, JsonElement oArgs) => strName switch
    {
        "slice" => StrSlice(oArgs),
        "stage_for_studio" => StrStage(oArgs),
        _ => throw new ToolError($"Unknown tool '{strName}'."),
    };

    public sealed class ToolError(string strMessage) : Exception(strMessage);

    private static string? StrOpt(JsonElement oArgs, string strKey)
        => oArgs.TryGetProperty(strKey, out JsonElement o) && o.ValueKind == JsonValueKind.String
            ? o.GetString() : null;

    private static string StrReq(JsonElement oArgs, string strKey)
        => StrOpt(oArgs, strKey) ?? throw new ToolError($"Missing required argument '{strKey}'.");

    private static string StrSlice(JsonElement oArgs)
    {
        var astrFilaments = new List<string>();
        if (oArgs.TryGetProperty("filaments", out JsonElement oFil) && oFil.ValueKind == JsonValueKind.Array)
            foreach (JsonElement o in oFil.EnumerateArray())
                if (o.GetString() is string s)
                    astrFilaments.Add(s);

        var oOverrides = new Dictionary<string, string>(StringComparer.Ordinal);
        if (oArgs.TryGetProperty("overrides", out JsonElement oOv) && oOv.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty oProp in oOv.EnumerateObject())
                if (oProp.Value.GetString() is string s)
                    oOverrides[oProp.Name] = s;

        SliceOperation.Outcome oOutcome;
        try
        {
            oOutcome = SliceOperation.Execute(new SliceOperation.Request
            {
                StrModel = StrReq(oArgs, "model"),
                StrMachine = StrReq(oArgs, "machine"),
                StrProcess = StrReq(oArgs, "process"),
                AstrFilaments = astrFilaments,
                StrOutDir = StrReq(oArgs, "outdir"),
                StrSlicer = StrOpt(oArgs, "slicer")
                    ?? Environment.GetEnvironmentVariable("OBCSLICE_ORCA"),
                StrDesignSidecar = StrOpt(oArgs, "design_sidecar"),
                StrDesignRunId = StrOpt(oArgs, "design_run_id") ?? "unknown",
                StrSlicerCommit = StrOpt(oArgs, "slicer_commit"),
                NPlate = oArgs.TryGetProperty("plate", out JsonElement oPlate)
                    && oPlate.ValueKind == JsonValueKind.Number ? oPlate.GetInt32() : 0,
                Overrides = oOverrides,
                BDryRun = oArgs.TryGetProperty("dry_run", out JsonElement oDry)
                    && oDry.ValueKind == JsonValueKind.True,
            });
        }
        catch (Exception e) when (e is InvalidDataException or FileNotFoundException or ArgumentException)
        {
            throw new ToolError(e.Message);
        }

        var oResult = new Dictionary<string, object?>
        {
            ["record_path"] = oOutcome.StrRecordPath,
            ["record_sha256"] = oOutcome.StrRecordSha256,
            ["exit_code"] = oOutcome.NExitCode,
            ["outputs"] = oOutcome.Outputs.Select(o => (object?)new Dictionary<string, object?>
            {
                ["name"] = o.StrName, ["sha256"] = o.StrSha256, ["plate"] = o.NPlate,
            }).ToList(),
            ["degradations"] = oOutcome.Degradations.Select(d => (object?)new Dictionary<string, object?>
            {
                ["code"] = d.StrCode, ["detail"] = d.StrDetail,
            }).ToList(),
        };
        return JsonSerializer.Serialize(oResult);
    }

    private static string StrStage(JsonElement oArgs)
    {
        string strGcode = StrReq(oArgs, "gcode");
        string strRecord = StrReq(oArgs, "slice_record");
        string strStaging = StrOpt(oArgs, "staging_dir")
            ?? Environment.GetEnvironmentVariable("OBCSLICE_STAGING")
            ?? throw new ToolError("No staging directory: pass staging_dir or set OBCSLICE_STAGING.");
        if (!File.Exists(strGcode))
            throw new ToolError($"G-code not found: {strGcode}");
        if (!File.Exists(strRecord))
            throw new ToolError($"Slice record not found: {strRecord}");

        // The record must attest exactly this G-code, and must not be degraded
        // in a way that forbids proposing from it (SLICE-RECORD.md rule 1).
        using JsonDocument oDoc = JsonDocument.Parse(File.ReadAllText(strRecord));
        JsonElement oRoot = oDoc.RootElement;
        if (StrOpt(oRoot, "record") != SliceRecord.StrSchema)
            throw new ToolError($"Not an {SliceRecord.StrSchema} record: {strRecord}");

        foreach (JsonElement oD in oRoot.GetProperty("degradations").EnumerateArray())
        {
            string? strCode = StrOpt(oD, "code");
            if (strCode is "slice_failed" or "dry_run" or "output_missing")
                throw new ToolError(
                    $"Record carries degradation '{strCode}' — refusing to stage from it.");
        }

        string strGcodeSha = SliceRecord.StrHashFile(strGcode);
        bool bAttested = false;
        foreach (JsonElement oOut in oRoot.GetProperty("result").GetProperty("outputs").EnumerateArray())
            if (StrOpt(oOut, "sha256") == strGcodeSha)
                bAttested = true;
        if (!bAttested)
            throw new ToolError(
                $"sha256(gcode)={strGcodeSha} is not among the record's attested outputs — " +
                "refusing to stage unattested G-code.");

        Directory.CreateDirectory(strStaging);
        string strRecordSha = SliceRecord.StrHashFile(strRecord);
        string strGcodeDest = Path.Combine(strStaging, strGcodeSha + ".gcode");
        string strRecordDest = Path.Combine(strStaging, strRecordSha + ".slice.json");
        File.Copy(strGcode, strGcodeDest, overwrite: true);
        File.Copy(strRecord, strRecordDest, overwrite: true);

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["staged_gcode"] = strGcodeDest,
            ["staged_record"] = strRecordDest,
            ["gcode_sha256"] = strGcodeSha,
            ["record_sha256"] = strRecordSha,
            ["next"] = "Propose gcode_upload then print_start in AdvancedStudio; both require human approval.",
        });
    }
}
