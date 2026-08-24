using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using ObcSlice;

namespace ObcSlicerUi;

/// <summary>Loopback HTTP server driving the pipeline. Handlers are pure
/// (request JSON in → response JSON out) so they are testable without sockets.
/// Every response carries the underlying record/degradations verbatim — the UI's
/// job is to show them loudly, never to summarize them away.</summary>
public sealed class Server
{
    private readonly string _strUiHtmlPath;
    private readonly string _strConfigPath;

    public Server(string strUiHtmlPath, string strConfigPath)
    {
        _strUiHtmlPath = strUiHtmlPath;
        _strConfigPath = strConfigPath;
    }

    public void Run(int nPort)
    {
        using var oListener = new HttpListener();
        oListener.Prefixes.Add($"http://127.0.0.1:{nPort}/");
        oListener.Start();
        Console.WriteLine($"obcslicer-ui: http://127.0.0.1:{nPort}/  (Ctrl+C to stop)");
        while (true)
        {
            HttpListenerContext oCtx = oListener.GetContext();
            try
            {
                Handle(oCtx);
            }
            catch (Exception e)
            {
                TryWrite(oCtx, 500, "application/json",
                    JsonSerializer.Serialize(new { error = e.Message }));
            }
        }
    }

    private void Handle(HttpListenerContext oCtx)
    {
        string strPath = oCtx.Request.Url?.AbsolutePath ?? "/";
        if (oCtx.Request.HttpMethod == "GET" && strPath == "/")
        {
            TryWrite(oCtx, 200, "text/html; charset=utf-8", File.ReadAllText(_strUiHtmlPath));
            return;
        }
        if (oCtx.Request.HttpMethod == "GET" && strPath == "/api/config")
        {
            TryWrite(oCtx, 200, "application/json",
                File.Exists(_strConfigPath) ? File.ReadAllText(_strConfigPath) : "{}");
            return;
        }

        string strBody;
        using (var oReader = new StreamReader(oCtx.Request.InputStream, Encoding.UTF8))
            strBody = oReader.ReadToEnd();

        (int nStatus, string strJson) = strPath switch
        {
            "/api/config" => SaveConfig(strBody),
            "/api/fieldcompile" => FieldCompile(strBody),
            "/api/compile3mf" => Compile3mf(strBody),
            "/api/slice" => Slice(strBody),
            "/api/stage" => StageOrPropose("stage_for_studio", strBody),
            "/api/propose-upload" => StageOrPropose("propose_upload", strBody),
            "/api/propose-print" => StageOrPropose("propose_print", strBody),
            "/api/studio-state" => StudioState(strBody),
            _ => (404, JsonSerializer.Serialize(new { error = $"no route {strPath}" })),
        };
        TryWrite(oCtx, nStatus, "application/json", strJson);
    }

    // ---- handlers (public static: exercised directly by tests) ----------------

    private (int, string) SaveConfig(string strBody)
    {
        JsonDocument.Parse(strBody); // validate before persisting
        Directory.CreateDirectory(Path.GetDirectoryName(_strConfigPath)!);
        File.WriteAllText(_strConfigPath, strBody);
        return (200, "{\"saved\":true}");
    }

    /// <summary>Field compile via the fieldengine executable (subprocess — it
    /// needs the PicoGK native stack). Body: { spec_path, outdir, spec_text?,
    /// fieldengine? }. When spec_text is present it is written to spec_path first
    /// (the editor's save), after validating it parses as JSON.</summary>
    public static (int, string) FieldCompile(string strBody)
    {
        using JsonDocument oDoc = JsonDocument.Parse(strBody);
        string strSpec = StrReq(oDoc.RootElement, "spec_path");
        string strOutDir = StrReq(oDoc.RootElement, "outdir");
        if (StrOpt(oDoc.RootElement, "spec_text") is string strText && strText.Length > 0)
        {
            try { JsonDocument.Parse(strText); }
            catch (JsonException e)
            {
                return (422, JsonSerializer.Serialize(new { error = "spec is not valid JSON: " + e.Message }));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(strSpec))!);
            File.WriteAllText(strSpec, strText);
        }
        string strEngine = StrOpt(oDoc.RootElement, "fieldengine")
            ?? Environment.GetEnvironmentVariable("OBCSLICE_FIELDENGINE")
            ?? StrProbeFieldEngine();

        var oPsi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (strEngine.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            oPsi.FileName = "dotnet";
            foreach (string s in new[] { "run", "--project", strEngine, "-c", "Release", "--" })
                oPsi.ArgumentList.Add(s);
        }
        else
        {
            oPsi.FileName = strEngine;
        }
        foreach (string s in new[] { "compile", "--spec", strSpec, "--outdir", strOutDir })
            oPsi.ArgumentList.Add(s);

        using Process oP = Process.Start(oPsi)!;
        string strOut = oP.StandardOutput.ReadToEnd();
        string strErr = oP.StandardError.ReadToEnd();
        oP.WaitForExit();

        string strRecordPath = Path.Combine(strOutDir, "field.json");
        return (oP.ExitCode == 0 ? 200 : 422, JsonSerializer.Serialize(new
        {
            exit_code = oP.ExitCode,
            stdout = strOut,
            stderr = strErr,
            record = ORecordOrNull(strRecordPath),
            manifest_path = Path.Combine(strOutDir, "manifest.json"),
        }));
    }

    /// <summary>obc3mf in-process. Body: { manifest_path, allow_open_mesh? }.</summary>
    public static (int, string) Compile3mf(string strBody)
    {
        using JsonDocument oDoc = JsonDocument.Parse(strBody);
        string strManifest = StrReq(oDoc.RootElement, "manifest_path");
        var aArgs = new List<string> { "compile", "--manifest", strManifest };
        if (oDoc.RootElement.TryGetProperty("allow_open_mesh", out JsonElement oA)
            && oA.ValueKind == JsonValueKind.True)
            aArgs.Add("--allow-open-mesh");

        (int nExit, string strConsole) = NCaptured(() => Obc3mf.Program.Main(aArgs.ToArray()));
        string strDir = Path.GetDirectoryName(Path.GetFullPath(strManifest))!;
        string? strOutput = StrManifestOutput(strManifest, strDir);
        return (nExit == 0 ? 200 : 422, JsonSerializer.Serialize(new
        {
            exit_code = nExit,
            console = strConsole,
            threemf_path = strOutput,
            record = strOutput is null ? null : ORecordOrNull(strOutput + ".project.json"),
        }));
    }

    /// <summary>obcslice in-process. Body: { model, machine, process, filaments[],
    /// outdir, slicer?, design_sidecar?, dry_run? }.</summary>
    public static (int, string) Slice(string strBody)
    {
        using JsonDocument oDoc = JsonDocument.Parse(strBody);
        JsonElement o = oDoc.RootElement;
        var aFilaments = new List<string>();
        if (o.TryGetProperty("filaments", out JsonElement oF) && oF.ValueKind == JsonValueKind.Array)
            foreach (JsonElement e in oF.EnumerateArray())
                if (e.GetString() is string s && s.Length > 0)
                    aFilaments.Add(s);
        try
        {
            SliceOperation.Outcome oOutcome = SliceOperation.Execute(new SliceOperation.Request
            {
                StrModel = StrReq(o, "model"),
                StrMachine = StrReq(o, "machine"),
                StrProcess = StrReq(o, "process"),
                AstrFilaments = aFilaments,
                StrOutDir = StrReq(o, "outdir"),
                StrSlicer = StrOpt(o, "slicer") ?? Environment.GetEnvironmentVariable("OBCSLICE_ORCA"),
                StrDesignSidecar = StrOpt(o, "design_sidecar"),
                BDryRun = o.TryGetProperty("dry_run", out JsonElement oD) && oD.ValueKind == JsonValueKind.True,
            });
            return (oOutcome.NExitCode == 0 ? 200 : 422, JsonSerializer.Serialize(new
            {
                exit_code = oOutcome.NExitCode,
                record_path = oOutcome.StrRecordPath,
                record = ORecordOrNull(oOutcome.StrRecordPath),
                gcode = oOutcome.Outputs.Select(g => new { g.StrName, g.StrSha256, g.NPlate }),
                gcode_stats = OGcodeStats(StrReq(o, "outdir")),
            }));
        }
        catch (Exception e) when (e is InvalidDataException or FileNotFoundException or ArgumentException)
        {
            return (422, JsonSerializer.Serialize(new { error = e.Message }));
        }
    }

    /// <summary>MCP tools reused directly. Body = the tool's arguments object.</summary>
    public static (int, string) StageOrPropose(string strTool, string strBody)
    {
        using JsonDocument oDoc = JsonDocument.Parse(strBody);
        try
        {
            return (200, ObcSlicerMcp.Tools.StrCall(strTool, oDoc.RootElement));
        }
        catch (ObcSlicerMcp.Tools.ToolError e)
        {
            return (422, JsonSerializer.Serialize(new { error = e.Message }));
        }
    }

    private static (int, string) StudioState(string strBody)
    {
        string strBase = "http://127.0.0.1:8770";
        try
        {
            using var oClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            string strState = oClient.GetStringAsync(strBase + "/api/state").GetAwaiter().GetResult();
            string strPending = oClient.GetStringAsync(strBase + "/api/agent/pending").GetAwaiter().GetResult();
            return (200, $"{{\"state\":{strState},\"pending\":{strPending}}}");
        }
        catch (Exception e)
        {
            return (200, JsonSerializer.Serialize(new { unreachable = e.Message }));
        }
    }

    // ---- helpers --------------------------------------------------------------

    private static string StrReq(JsonElement o, string strKey)
        => o.TryGetProperty(strKey, out JsonElement e) && e.GetString() is string s && s.Length > 0
            ? s : throw new InvalidDataException($"missing '{strKey}'");

    private static string? StrOpt(JsonElement o, string strKey)
        => o.TryGetProperty(strKey, out JsonElement e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() : null;

    private static object? ORecordOrNull(string strPath)
        => File.Exists(strPath)
            ? JsonSerializer.Deserialize<object>(File.ReadAllText(strPath)) : null;

    private static string? StrManifestOutput(string strManifest, string strDir)
    {
        try
        {
            using JsonDocument oDoc = JsonDocument.Parse(File.ReadAllText(strManifest));
            string? strOut = oDoc.RootElement.GetProperty("output").GetString();
            return strOut is null ? null
                : Path.IsPathRooted(strOut) ? strOut : Path.Combine(strDir, strOut);
        }
        catch { return null; }
    }

    private static object OGcodeStats(string strOutDir)
    {
        var aStats = new List<object>();
        if (!Directory.Exists(strOutDir))
            return aStats;
        foreach (string strGcode in Directory.EnumerateFiles(strOutDir, "plate_*.gcode"))
        {
            int nT0 = 0, nT1 = 0;
            string strLayers = "?", strFilaments = "?";
            foreach (string strLine in File.ReadLines(strGcode))
            {
                if (strLine.StartsWith("T0")) nT0++;
                else if (strLine.StartsWith("T1")) nT1++;
                else if (strLine.StartsWith("; total layer number:"))
                    strLayers = strLine[21..].Trim();
                else if (strLine.StartsWith("; filament:"))
                    strFilaments = strLine[11..].Trim();
            }
            aStats.Add(new { file = Path.GetFileName(strGcode), layers = strLayers,
                filaments = strFilaments, toolchanges_t0 = nT0, toolchanges_t1 = nT1 });
        }
        return aStats;
    }

    private static (int, string) NCaptured(Func<int> oRun)
    {
        var oOldOut = Console.Out;
        var oOldErr = Console.Error;
        var oSb = new StringWriter();
        try
        {
            Console.SetOut(oSb);
            Console.SetError(oSb);
            int nExit = oRun();
            return (nExit, oSb.ToString());
        }
        finally
        {
            Console.SetOut(oOldOut);
            Console.SetError(oOldErr);
        }
    }

    private static string StrProbeFieldEngine()
    {
        // Repo-relative probes from the running binary's location upward.
        string strBase = AppContext.BaseDirectory;
        for (DirectoryInfo? oDir = new(strBase); oDir is not null; oDir = oDir.Parent)
        {
            string strExe = Path.Combine(oDir.FullName, "src", "fieldengine",
                "bin", "Release", "net9.0", "fieldengine.exe");
            if (File.Exists(strExe))
                return strExe;
            string strProj = Path.Combine(oDir.FullName, "src", "fieldengine", "fieldengine.csproj");
            if (File.Exists(strProj))
                return strProj;
        }
        throw new InvalidDataException(
            "fieldengine not found — set OBCSLICE_FIELDENGINE to its exe or csproj path.");
    }

    private static void TryWrite(HttpListenerContext oCtx, int nStatus, string strContentType, string strBody)
    {
        try
        {
            byte[] ab = Encoding.UTF8.GetBytes(strBody);
            oCtx.Response.StatusCode = nStatus;
            oCtx.Response.ContentType = strContentType;
            oCtx.Response.ContentLength64 = ab.Length;
            oCtx.Response.OutputStream.Write(ab, 0, ab.Length);
            oCtx.Response.Close();
        }
        catch { /* client went away */ }
    }
}
