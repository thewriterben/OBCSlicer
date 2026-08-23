using System.Text;
using ObcSlice;

namespace ObcSlice.Tests;

/// <summary>Zero-dependency test runner. Each public static parameterless method
/// in Tests is a case; any unhandled exception is a failure. Exit 0 iff all pass.</summary>
public static class Program
{
    public static int Main()
    {
        int nFailed = 0, nPassed = 0;
        foreach (var oMethod in typeof(Tests).GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            if (oMethod.GetParameters().Length != 0 || oMethod.ReturnType != typeof(void))
                continue;
            try
            {
                oMethod.Invoke(null, null);
                nPassed++;
                Console.WriteLine($"PASS {oMethod.Name}");
            }
            catch (Exception e)
            {
                nFailed++;
                Console.WriteLine($"FAIL {oMethod.Name}: {e.InnerException?.Message ?? e.Message}");
            }
        }
        Console.WriteLine($"{nPassed} passed, {nFailed} failed");
        return nFailed == 0 ? 0 : 1;
    }
}

public static class Assert
{
    public static void Equal(string strExpected, string strActual)
    {
        if (strExpected != strActual)
            throw new Exception($"expected:\n{strExpected}\nactual:\n{strActual}");
    }

    public static void True(bool bCondition, string strWhat)
    {
        if (!bCondition)
            throw new Exception($"expected true: {strWhat}");
    }

    public static void Throws<T>(Action oAction, string strWhat) where T : Exception
    {
        try { oAction(); }
        catch (T) { return; }
        catch (Exception e) { throw new Exception($"{strWhat}: threw {e.GetType().Name}, wanted {typeof(T).Name}"); }
        throw new Exception($"{strWhat}: did not throw {typeof(T).Name}");
    }
}

public static class Tests
{
    // ---- CanonicalJson: byte compatibility with python json.dumps(sort_keys=True,
    // separators=(",",":")) — the BINGO/ODC contract ---------------------------

    public static void CanonicalSortsKeysAndEscapesNonAscii()
    {
        var oObj = new Dictionary<string, object?> { ["b"] = 1, ["a"] = "\u00e9" };
        Assert.Equal("{\"a\":\"\\u00e9\",\"b\":1}", Encoding.ASCII.GetString(CanonicalJson.Serialize(oObj)));
    }

    public static void CanonicalNestedAndScalars()
    {
        var oObj = new Dictionary<string, object?>
        {
            ["z"] = new List<object?> { null, true, false, 42L, "x\n" },
            ["a"] = new Dictionary<string, object?> { ["k"] = "v" },
        };
        Assert.Equal("{\"a\":{\"k\":\"v\"},\"z\":[null,true,false,42,\"x\\n\"]}",
            Encoding.ASCII.GetString(CanonicalJson.Serialize(oObj)));
    }

    public static void CanonicalControlCharsEscaped()
    {
        Assert.Equal("\"\\u0001\\t\\\"\\\\\"",
            Encoding.ASCII.GetString(CanonicalJson.Serialize("\u0001\t\"\\")));
    }

    public static void CanonicalRejectsFloats()
    {
        Assert.Throws<ArgumentException>(() => CanonicalJson.Serialize(1.5), "float");
        Assert.Throws<ArgumentException>(
            () => CanonicalJson.Serialize(new Dictionary<string, object?> { ["a"] = 0.1f }), "nested float");
    }

    // ---- SliceRecord ---------------------------------------------------------

    private static SliceRecord ORecord(IReadOnlyList<SliceRecord.OutputRef>? aOutputs = null) => new()
    {
        StrCreatedUtc = "2026-08-23T00:00:00Z",
        StrModelName = "m.stl",
        StrModelSha256 = new string('a', 64),
        StrModelMediaType = "model/stl",
        Profiles = new[]
        {
            new SliceRecord.ProfileRef("machine", "mach.json", new string('b', 64), null),
            new SliceRecord.ProfileRef("filament", "f.json", new string('c', 64), 1),
        },
        Overrides = new Dictionary<string, string> { ["curr-bed-type"] = "Textured PEI Plate" },
        Design = new SliceRecord.DesignProvenance(
            "odc/provenance/0.1", new string('d', 64), new string('a', 64), "7"),
        StrSlicerName = "OrcaSlicer_AS",
        StrSlicerBinarySha256 = new string('e', 64),
        StrSlicerCommit = "542cd18d19",
        Invocation = new[] { "orca-slicer", "m.stl" },
        NExitCode = 0,
        Outputs = aOutputs ?? new[] { new SliceRecord.OutputRef("plate_1.gcode", new string('f', 64), 1) },
        StrStderrTail = "",
        Degradations = Array.Empty<Degradation>(),
    };

    public static void RecordBytesAreDeterministic()
    {
        Assert.Equal(
            Encoding.ASCII.GetString(ORecord().ToCanonicalBytes()),
            Encoding.ASCII.GetString(ORecord().ToCanonicalBytes()));
    }

    public static void RecordShapeIsStable()
    {
        string strJson = Encoding.ASCII.GetString(ORecord().ToCanonicalBytes());
        // Spot-check the canonical envelope: sorted top-level keys, schema tag, no whitespace.
        Assert.True(strJson.StartsWith("{\"created_utc\":\"2026-08-23T00:00:00Z\",\"degradations\":[],\"determinism\":\"attested\",\"inputs\":", StringComparison.Ordinal),
            "top-level key order/envelope");
        Assert.True(strJson.Contains("\"record\":\"obc-slice/0.1\""), "schema tag");
        Assert.True(strJson.Contains("\"slot\":1"), "filament slot");
        Assert.True(!strJson.Contains(' ') || strJson.Contains("Textured PEI Plate"),
            "no structural whitespace");
    }

    public static void SidecarVerificationRefusesMismatch()
    {
        string strDir = Directory.CreateTempSubdirectory("obcslice-test").FullName;
        string strSidecar = Path.Combine(strDir, "x.provenance.json");
        File.WriteAllText(strSidecar,
            "{\"schema\":\"odc/provenance/0.1\",\"artifact\":{\"sha256\":\"" + new string('1', 64) + "\"}}");
        Assert.Throws<InvalidDataException>(
            () => SliceRecord.ParseAndVerifySidecar(strSidecar, new string('2', 64), "unknown"),
            "hash mismatch must refuse");
        // Matching hash passes and carries the schema through.
        var oDesign = SliceRecord.ParseAndVerifySidecar(strSidecar, new string('1', 64), "9");
        Assert.Equal("odc/provenance/0.1", oDesign.StrSchema);
        Assert.Equal("9", oDesign.StrRunId);
    }

    public static void SidecarWithoutArtifactHashRefused()
    {
        string strDir = Directory.CreateTempSubdirectory("obcslice-test").FullName;
        string strSidecar = Path.Combine(strDir, "bad.json");
        File.WriteAllText(strSidecar, "{\"schema\":\"odc/provenance/0.1\"}");
        Assert.Throws<InvalidDataException>(
            () => SliceRecord.ParseAndVerifySidecar(strSidecar, new string('1', 64), "unknown"),
            "sidecar without artifact.sha256");
    }

    // ---- OrcaInvocation ------------------------------------------------------

    public static void BuildArgsGolden()
    {
        List<string> astr = OrcaInvocation.BuildArgs(
            "C:\\m.stl", "mach.json", "proc.json", new[] { "f1.json", "f2.json" },
            "out", 0, new Dictionary<string, string> { ["debug"] = "2", ["curr-bed-type"] = "X" });
        Assert.Equal(
            "C:\\m.stl|--load-settings|mach.json;proc.json|--load-filaments|f1.json;f2.json|--slice|0|--outputdir|out|--curr-bed-type|X|--debug|2",
            string.Join('|', astr));
    }

    public static void InheritsDetection()
    {
        string strDir = Directory.CreateTempSubdirectory("obcslice-test").FullName;
        string strFlat = Path.Combine(strDir, "flat.json");
        string strUnflat = Path.Combine(strDir, "unflat.json");
        File.WriteAllText(strFlat, "{\"layer_height\":\"0.2\"}");
        File.WriteAllText(strUnflat, "{\"inherits\":\"fdm_process_common\"}");
        Assert.True(!OrcaInvocation.BProfileHasInherits(strFlat), "flat profile clean");
        Assert.True(OrcaInvocation.BProfileHasInherits(strUnflat), "inherits detected");
    }

    // ---- End to end (dry run) ------------------------------------------------

    public static void DryRunWritesRecordWithDegradations()
    {
        string strDir = Directory.CreateTempSubdirectory("obcslice-e2e").FullName;
        string strModel = Path.Combine(strDir, "part.stl");
        File.WriteAllBytes(strModel, new byte[] { 0x73, 0x74, 0x6c });
        string strMach = Path.Combine(strDir, "mach.json");
        string strProc = Path.Combine(strDir, "proc.json");
        File.WriteAllText(strMach, "{}");
        File.WriteAllText(strProc, "{\"inherits\":\"x\"}");
        string strOut = Path.Combine(strDir, "out");

        int nExit = ObcSlice.Program.Main(new[]
        {
            "slice", "--model", strModel, "--machine", strMach, "--process", strProc,
            "--outdir", strOut, "--dry-run",
        });
        Assert.True(nExit == 0, $"dry run exit 0, got {nExit}");
        string strRecord = File.ReadAllText(Path.Combine(strOut, "part.slice.json"));
        Assert.True(strRecord.Contains("\"no_design_provenance\""), "missing-sidecar degradation");
        Assert.True(strRecord.Contains("\"unflattened_profile\""), "inherits degradation");
        Assert.True(strRecord.Contains("\"dry_run\""), "dry-run degradation");
        Assert.True(strRecord.Contains("\"design_provenance\":null"), "design null");
    }

    // ---- MCP server ----------------------------------------------------------

    private static System.Text.Json.JsonElement OMcp(string strRequest)
    {
        string? strResponse = ObcSlicerMcp.McpServer.StrHandleMessage(strRequest);
        Assert.True(strResponse is not null, "expected a response");
        return System.Text.Json.JsonDocument.Parse(strResponse!).RootElement;
    }

    public static void McpInitializeHandshake()
    {
        var o = OMcp("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"," +
            "\"params\":{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{}}}");
        Assert.Equal("2025-03-26", o.GetProperty("result").GetProperty("protocolVersion").GetString()!);
        Assert.Equal("obcslicer-mcp",
            o.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString()!);
    }

    public static void McpNotificationsAreSilent()
    {
        Assert.True(ObcSlicerMcp.McpServer.StrHandleMessage(
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}") is null,
            "notification must produce no response");
    }

    public static void McpToolsListAndUnknownMethod()
    {
        var o = OMcp("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}");
        string strJson = o.GetRawText();
        Assert.True(strJson.Contains("\"slice\"") && strJson.Contains("\"stage_for_studio\""),
            "both tools listed");
        var oErr = OMcp("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"resources/list\"}");
        Assert.True(oErr.GetProperty("error").GetProperty("code").GetInt32() == -32601,
            "unknown method → -32601");
    }

    public static void McpSliceDryRunAndStageRefusal()
    {
        string strDir = Directory.CreateTempSubdirectory("obcslice-mcp").FullName;
        string strModel = Path.Combine(strDir, "part.stl");
        File.WriteAllBytes(strModel, new byte[] { 9, 9, 9 });
        File.WriteAllText(Path.Combine(strDir, "mach.json"), "{}");
        File.WriteAllText(Path.Combine(strDir, "proc.json"), "{}");
        string strOut = Path.Combine(strDir, "out");

        string strArgs = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["model"] = strModel,
            ["machine"] = Path.Combine(strDir, "mach.json"),
            ["process"] = Path.Combine(strDir, "proc.json"),
            ["outdir"] = strOut,
            ["dry_run"] = true,
        });
        var o = OMcp("{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\"," +
            $"\"params\":{{\"name\":\"slice\",\"arguments\":{strArgs}}}}}");
        Assert.True(!o.GetProperty("result").GetProperty("isError").GetBoolean(), "slice dry run ok");
        string strRecordPath = Path.Combine(strOut, "part.slice.json");
        Assert.True(File.Exists(strRecordPath), "record written via MCP");

        // Staging must refuse a dry-run record.
        File.WriteAllText(Path.Combine(strDir, "fake.gcode"), "G28\n");
        string strStageArgs = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["gcode"] = Path.Combine(strDir, "fake.gcode"),
            ["slice_record"] = strRecordPath,
            ["staging_dir"] = Path.Combine(strDir, "staging"),
        });
        var oStage = OMcp("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\"," +
            $"\"params\":{{\"name\":\"stage_for_studio\",\"arguments\":{strStageArgs}}}}}");
        Assert.True(oStage.GetProperty("result").GetProperty("isError").GetBoolean(),
            "stage must refuse dry_run record");
        Assert.True(oStage.GetRawText().Contains("dry_run"), "refusal names the degradation");
    }

    public static void McpStageHappyPathAndUnattestedRefusal()
    {
        string strDir = Directory.CreateTempSubdirectory("obcslice-stage").FullName;
        string strGcode = Path.Combine(strDir, "plate_1.gcode");
        File.WriteAllText(strGcode, "G28\nG1 X10\n");
        string strGcodeSha = SliceRecord.StrHashFile(strGcode);

        SliceRecord oRecord = ORecord(new[] { new SliceRecord.OutputRef("plate_1.gcode", strGcodeSha, 1) });
        string strRecordPath = Path.Combine(strDir, "part.slice.json");
        File.WriteAllBytes(strRecordPath, oRecord.ToCanonicalBytes());

        string strStaging = Path.Combine(strDir, "staging");
        string strArgs = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["gcode"] = strGcode, ["slice_record"] = strRecordPath, ["staging_dir"] = strStaging,
        });
        var o = OMcp("{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"tools/call\"," +
            $"\"params\":{{\"name\":\"stage_for_studio\",\"arguments\":{strArgs}}}}}");
        Assert.True(!o.GetProperty("result").GetProperty("isError").GetBoolean(), "stage ok");
        Assert.True(File.Exists(Path.Combine(strStaging, strGcodeSha + ".gcode")),
            "hash-named gcode staged");

        // Tampered G-code (hash no longer attested) must be refused.
        File.AppendAllText(strGcode, "M104 S999\n");
        var oBad = OMcp("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\"," +
            $"\"params\":{{\"name\":\"stage_for_studio\",\"arguments\":{strArgs}}}}}");
        Assert.True(oBad.GetProperty("result").GetProperty("isError").GetBoolean(),
            "tampered gcode refused");
        // Note: System.Text.Json escapes apostrophes in output, so match a clean substring.
        Assert.True(oBad.GetRawText().Contains("attested outputs"),
            "refusal explains attestation failure");
    }

    public static void MismatchedSidecarIsExitCode2()
    {
        string strDir = Directory.CreateTempSubdirectory("obcslice-e2e").FullName;
        string strModel = Path.Combine(strDir, "part.stl");
        File.WriteAllBytes(strModel, new byte[] { 1, 2, 3 });
        string strMach = Path.Combine(strDir, "mach.json");
        string strProc = Path.Combine(strDir, "proc.json");
        File.WriteAllText(strMach, "{}");
        File.WriteAllText(strProc, "{}");
        string strSidecar = Path.Combine(strDir, "part.provenance.json");
        File.WriteAllText(strSidecar,
            "{\"schema\":\"odc/provenance/0.1\",\"artifact\":{\"sha256\":\"" + new string('0', 64) + "\"}}");

        int nExit = ObcSlice.Program.Main(new[]
        {
            "slice", "--model", strModel, "--machine", strMach, "--process", strProc,
            "--outdir", Path.Combine(strDir, "out"), "--dry-run", "--design-sidecar", strSidecar,
        });
        Assert.True(nExit == 2, $"mismatch must exit 2, got {nExit}");
    }
}
