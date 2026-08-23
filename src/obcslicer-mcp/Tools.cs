using System.Security.Cryptography;
using System.Text.Json;
using ObcSlice;

namespace ObcSlicerMcp;

/// <summary>Tools of v0.2. `slice` executes locally (it writes only into its own
/// output directory). `stage_for_studio` copies hash-named artifacts into
/// AdvancedStudio's bounded staging directory. `propose_upload`/`propose_print`
/// POST to AdvancedStudio's `/api/propose` (contract read from studio-core
/// source: guard.py, staging.py, app.py L624) — they only REGISTER a pending
/// confirmation; a human approves in the dashboard, and no approval capability
/// exists here (ADR-0004, AdvancedStudio's own safety contract).</summary>
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
        new Dictionary<string, object?>
        {
            ["name"] = "propose_upload",
            ["description"] =
                "Propose uploading a staged G-code file to the printer via AdvancedStudio's " +
                "/api/propose. Registers a pending confirmation that a HUMAN must approve in the " +
                "studio dashboard — this tool cannot approve anything. The filename is relative " +
                "to AdvancedStudio's staging directory. Does not start a print.",
            ["inputSchema"] = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["filename"] = OStr("Staged file name, relative to the staging directory (from stage_for_studio)."),
                    ["design_sha256"] = OStr("Design artifact hash the G-code was sliced from (64 hex)."),
                    ["studio_url"] = OStr("AdvancedStudio base URL (default env OBCSLICE_STUDIO or http://127.0.0.1:8770)."),
                },
                ["required"] = new List<object?> { "filename" },
            },
        },
        new Dictionary<string, object?>
        {
            ["name"] = "propose_print",
            ["description"] =
                "Propose starting a print of an uploaded file via AdvancedStudio's /api/propose. " +
                "Registers a pending confirmation that a HUMAN must approve in the studio " +
                "dashboard — this tool cannot approve or start anything itself.",
            ["inputSchema"] = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["filename"] = OStr("Job file name as the printer knows it."),
                    ["design_sha256"] = OStr("Design artifact hash the job came from (64 hex)."),
                    ["studio_url"] = OStr("AdvancedStudio base URL (default env OBCSLICE_STUDIO or http://127.0.0.1:8770)."),
                },
                ["required"] = new List<object?> { "filename" },
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
        "propose_upload" => StrPropose(oArgs, "gcode_upload"),
        "propose_print" => StrPropose(oArgs, "print_start"),
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

        // Design anchor for the filename: the ODC design artifact when the record
        // has one, else the sliced model itself — AdvancedStudio warns on approval
        // when a filename stops carrying the design hash's first 12 hex chars
        // (staging.py carries_design_hash), so the name leads with it.
        string? strDesignSha = null;
        if (oRoot.GetProperty("inputs").TryGetProperty("design_provenance", out JsonElement oDp)
            && oDp.ValueKind == JsonValueKind.Object)
            strDesignSha = StrOpt(oDp, "artifact_sha256");
        strDesignSha ??= oRoot.GetProperty("inputs").GetProperty("model").GetProperty("sha256").GetString();

        Directory.CreateDirectory(strStaging);
        string strRecordSha = SliceRecord.StrHashFile(strRecord);
        string strStagedName = $"{strDesignSha![..12]}-{strGcodeSha[..12]}.gcode";
        string strGcodeDest = Path.Combine(strStaging, strStagedName);
        string strRecordDest = Path.Combine(strStaging, strRecordSha + ".slice.json");
        File.Copy(strGcode, strGcodeDest, overwrite: true);
        File.Copy(strRecord, strRecordDest, overwrite: true);

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["staged_gcode"] = strGcodeDest,
            ["staged_filename"] = strStagedName,
            ["staged_record"] = strRecordDest,
            ["gcode_sha256"] = strGcodeSha,
            ["design_sha256"] = strDesignSha,
            ["record_sha256"] = strRecordSha,
            ["next"] = "Call propose_upload with staged_filename and design_sha256; a human approves in the dashboard.",
        });
    }

    private static string StrPropose(JsonElement oArgs, string strAction)
    {
        string strFilename = StrReq(oArgs, "filename");
        string? strDesign = StrOpt(oArgs, "design_sha256");
        string strBase = StrOpt(oArgs, "studio_url")
            ?? Environment.GetEnvironmentVariable("OBCSLICE_STUDIO")
            ?? "http://127.0.0.1:8770";
        string? strToken = Environment.GetEnvironmentVariable("OBCSLICE_STUDIO_TOKEN");

        var oParams = new Dictionary<string, object?> { ["filename"] = strFilename };
        if (!string.IsNullOrEmpty(strDesign))
            oParams["design_artifact_sha256"] = strDesign;
        string strBody = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["action"] = strAction, ["params"] = oParams,
        });

        using var oClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var oRequest = new HttpRequestMessage(HttpMethod.Post, strBase.TrimEnd('/') + "/api/propose")
        {
            Content = new StringContent(strBody, System.Text.Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(strToken))
            oRequest.Headers.Add("X-Studio-Token", strToken);

        HttpResponseMessage oResponse;
        string strResponseBody;
        try
        {
            oResponse = oClient.Send(oRequest);
            strResponseBody = new StreamReader(oResponse.Content.ReadAsStream()).ReadToEnd();
        }
        catch (HttpRequestException e)
        {
            throw new ToolError(
                $"AdvancedStudio unreachable at {strBase}: {e.Message}. Is studio-core running?");
        }
        if (!oResponse.IsSuccessStatusCode)
            throw new ToolError(
                $"/api/propose {strAction} refused ({(int)oResponse.StatusCode}): {strResponseBody}");
        return strResponseBody; // confirmation_id, will_run, message — verbatim from the studio
    }
}
