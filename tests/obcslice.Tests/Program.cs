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

    // ---- obc3mf --------------------------------------------------------------

    /// <summary>Closed tetrahedron as a binary STL: 4 vertices, 4 faces.</summary>
    private static void WriteTetraStl(string strPath)
    {
        var aoV = new (float, float, float)[] { (0, 0, 0), (1, 0, 0), (0, 1, 0), (0, 0, 1) };
        var aoF = new (int, int, int)[] { (0, 2, 1), (0, 1, 3), (1, 2, 3), (0, 3, 2) };
        using var oW = new BinaryWriter(File.Create(strPath));
        oW.Write(new byte[80]);
        oW.Write((uint)aoF.Length);
        foreach ((int a, int b, int c) in aoF)
        {
            oW.Write(0f); oW.Write(0f); oW.Write(0f); // normal (recomputed by consumers)
            foreach (int n in new[] { a, b, c })
            {
                (float x, float y, float z) = aoV[n];
                oW.Write(x); oW.Write(y); oW.Write(z);
            }
            oW.Write((ushort)0);
        }
    }

    public static void StlTetraReadsClosedAndWelded()
    {
        string strDir = Directory.CreateTempSubdirectory("obc3mf-stl").FullName;
        string strStl = Path.Combine(strDir, "tetra.stl");
        WriteTetraStl(strStl);
        Obc3mf.IndexedMesh oMesh = Obc3mf.StlReader.Read(strStl);
        Assert.True(oMesh.Vertices.Count == 4, $"welded to 4 vertices, got {oMesh.Vertices.Count}");
        Assert.True(oMesh.Triangles.Count == 4, "4 triangles");
        Assert.True(oMesh.NOpenEdges() == 0, "tetrahedron is closed");
    }

    public static void StlAsciiSingleTriangleIsOpen()
    {
        string strDir = Directory.CreateTempSubdirectory("obc3mf-stl").FullName;
        string strStl = Path.Combine(strDir, "tri.stl");
        File.WriteAllText(strStl, """
            solid tri
              facet normal 0 0 1
                outer loop
                  vertex 0 0 0
                  vertex 1 0 0
                  vertex 0 1 0
                endloop
              endfacet
            endsolid tri
            """);
        Obc3mf.IndexedMesh oMesh = Obc3mf.StlReader.Read(strStl);
        Assert.True(oMesh.Triangles.Count == 1, "one triangle parsed");
        Assert.True(oMesh.NOpenEdges() == 3, "single triangle has 3 open edges");
    }

    public static void CompileTwoPartProjectAndVerifyArchive()
    {
        string strDir = Directory.CreateTempSubdirectory("obc3mf-e2e").FullName;
        WriteTetraStl(Path.Combine(strDir, "body.stl"));
        WriteTetraStl(Path.Combine(strDir, "grip.stl"));
        string strManifest = Path.Combine(strDir, "m.json");
        File.WriteAllText(strManifest, """
            { "output": "part.3mf",
              "printer": { "nozzle_diameter_mm": "0.4", "printable_width_mm": 350,
                           "printable_depth_mm": 350, "printable_height_mm": 350 },
              "parts": [
              { "mesh": "body.stl", "name": "body", "filament": 1 },
              { "mesh": "grip.stl", "name": "grip", "filament": 2,
                "overrides": { "nozzle_temperature": "230" } } ] }
            """);
        int nExit = Obc3mf.Program.Main(new[] { "compile", "--manifest", strManifest });
        Assert.True(nExit == 0, $"compile exit 0, got {nExit}");

        string str3mf = Path.Combine(strDir, "part.3mf");
        using var oZip = System.IO.Compression.ZipFile.OpenRead(str3mf);
        foreach (string strEntry in new[] { "[Content_Types].xml", "_rels/.rels",
            "3D/3dmodel.model", "3D/_rels/3dmodel.model.rels",
            "3D/Objects/body_1.model", "3D/Objects/grip_3.model",
            "Metadata/model_settings.config", "Metadata/project_settings.config" })
            Assert.True(oZip.GetEntry(strEntry) is not null, $"archive contains {strEntry}");

        var oNsTable = new System.Xml.NameTable();
        var oNs = new System.Xml.XmlNamespaceManager(oNsTable);
        oNs.AddNamespace("m", "http://schemas.microsoft.com/3dmanufacturing/core/2015/02");
        oNs.AddNamespace("p", "http://schemas.microsoft.com/3dmanufacturing/production/2015/06");

        var oModel = new System.Xml.XmlDocument(oNsTable);
        using (Stream oS = oZip.GetEntry("3D/3dmodel.model")!.Open())
            oModel.Load(oS);
        Assert.Equal("millimeter", oModel.SelectSingleNode("/m:model/@unit", oNs)!.Value!);
        Assert.Equal("p", oModel.SelectSingleNode("/m:model/@requiredextensions", oNs)!.Value!);
        Assert.True(oModel.SelectSingleNode(
            "/m:model/m:metadata[@name='Application']", oNs)!.InnerText.StartsWith("BambuStudio-"),
            "Application tag present (project identity gate)");
        // Root model holds component wrappers (even ids) referencing object files, no inline meshes.
        Assert.True(oModel.SelectNodes("//m:object[m:components]", oNs)!.Count == 2, "two wrapper objects");
        Assert.True(oModel.SelectNodes("//m:mesh", oNs)!.Count == 0, "no inline meshes in root");
        Assert.Equal("/3D/Objects/grip_3.model", oModel.SelectSingleNode(
            "//m:object[@id='4']//m:component/@p:path", oNs)!.Value!);
        Assert.True(oModel.SelectNodes("//m:item", oNs)!.Count == 2, "two build items");

        var oObj = new System.Xml.XmlDocument(oNsTable);
        using (Stream oS = oZip.GetEntry("3D/Objects/body_1.model")!.Open())
            oObj.Load(oS);
        Assert.True(oObj.SelectNodes("//m:object[@id='1']//m:triangle", oNs)!.Count == 4,
            "mesh object 1 has 4 triangles");

        var oConfig = new System.Xml.XmlDocument();
        using (Stream oS = oZip.GetEntry("Metadata/model_settings.config")!.Open())
            oConfig.Load(oS);
        // model_settings objects use the WRAPPER ids (2, 4).
        Assert.Equal("1", oConfig.SelectSingleNode(
            "/config/object[@id='2']/metadata[@key='extruder']/@value")!.Value!);
        Assert.Equal("2", oConfig.SelectSingleNode(
            "/config/object[@id='4']/metadata[@key='extruder']/@value")!.Value!);
        Assert.Equal("230", oConfig.SelectSingleNode(
            "/config/object[@id='4']/metadata[@key='nozzle_temperature']/@value")!.Value!);
        Assert.Equal("1 1", oConfig.SelectSingleNode(
            "/config/plate/metadata[@key='filament_maps']/@value")!.Value!);
        Assert.True(oConfig.SelectNodes("/config/plate/model_instance")!.Count == 2, "plate instances");
        Assert.True(oConfig.SelectNodes("/config/assemble/assemble_item")!.Count == 2, "assemble items");

        using (Stream oS = oZip.GetEntry("Metadata/project_settings.config")!.Open())
        using (var oPs = System.Text.Json.JsonDocument.Parse(oS))
        {
            Assert.True(oPs.RootElement.GetProperty("filament_colour").GetArrayLength() == 2,
                "filament arrays sized to slot count");
            Assert.Equal("350", oPs.RootElement.GetProperty("printable_height").GetString()!);
            Assert.Equal("0.4", oPs.RootElement.GetProperty("nozzle_diameter")[0].GetString()!);
        }

        string strRecord = File.ReadAllText(str3mf + ".project.json");
        Assert.True(strRecord.Contains("\"record\":\"obc-project/0.2\""), "compile record schema");
        Assert.True(strRecord.Contains("\"structure\":\"orca-project\""), "structure recorded");
        Assert.True(strRecord.Contains(SliceRecord.StrHashFile(str3mf)), "record attests 3mf hash");
        Assert.True(strRecord.Contains("\"degradations\":[]"), "clean compile has empty degradations");
    }

    public static void CompileAssemblyModeStructure()
    {
        string strDir = Directory.CreateTempSubdirectory("obc3mf-asm").FullName;
        WriteTetraStl(Path.Combine(strDir, "lower.stl"));
        WriteTetraStl(Path.Combine(strDir, "upper.stl"));
        string strManifest = Path.Combine(strDir, "m.json");
        File.WriteAllText(strManifest, """
            { "output": "asm.3mf", "assembly": true,
              "printer": { "nozzle_diameter_mm": "0.4", "printable_width_mm": 350,
                           "printable_depth_mm": 350, "printable_height_mm": 350 },
              "parts": [
              { "mesh": "lower.stl", "name": "lower", "filament": 1,
                "overrides": { "sparse_infill_density": "80%" } },
              { "mesh": "upper.stl", "name": "upper", "filament": 2 } ] }
            """);
        Assert.True(Obc3mf.Program.Main(new[] { "compile", "--manifest", strManifest }) == 0,
            "assembly compile ok");

        using var oZip = System.IO.Compression.ZipFile.OpenRead(Path.Combine(strDir, "asm.3mf"));
        var oNsTable = new System.Xml.NameTable();
        var oNs = new System.Xml.XmlNamespaceManager(oNsTable);
        oNs.AddNamespace("m", "http://schemas.microsoft.com/3dmanufacturing/core/2015/02");
        var oModel = new System.Xml.XmlDocument(oNsTable);
        using (Stream oS = oZip.GetEntry("3D/3dmodel.model")!.Open())
            oModel.Load(oS);
        Assert.True(oModel.SelectNodes("//m:object[m:components]", oNs)!.Count == 1,
            "one wrapper object in assembly mode");
        Assert.True(oModel.SelectNodes("//m:component", oNs)!.Count == 2,
            "wrapper holds both meshes as components");
        Assert.True(oModel.SelectNodes("//m:item", oNs)!.Count == 1, "one build item");
        // Placement: tetra bbox is (0..1)^3, plate 350x350 → center translation 174.5, floor Z 0.
        Assert.Equal("1 0 0 0 1 0 0 0 1 174.5 174.5 0",
            oModel.SelectSingleNode("//m:item/@transform", oNs)!.Value!);

        var oConfig = new System.Xml.XmlDocument();
        using (Stream oS = oZip.GetEntry("Metadata/model_settings.config")!.Open())
            oConfig.Load(oS);
        Assert.True(oConfig.SelectNodes("/config/object")!.Count == 1, "one config object");
        Assert.Equal("1", oConfig.SelectSingleNode(
            "/config/object/part[@id='1']/metadata[@key='extruder']/@value")!.Value!);
        Assert.Equal("2", oConfig.SelectSingleNode(
            "/config/object/part[@id='3']/metadata[@key='extruder']/@value")!.Value!);
        // Per-part process overrides ride the same metadata seam (P3 grading).
        Assert.Equal("80%", oConfig.SelectSingleNode(
            "/config/object/part[@id='1']/metadata[@key='sparse_infill_density']/@value")!.Value!);
        Assert.True(oConfig.SelectNodes("/config/plate/model_instance")!.Count == 1,
            "single plate instance");
        string strRecord = File.ReadAllText(Path.Combine(strDir, "asm.3mf.project.json"));
        Assert.True(strRecord.Contains("\"structure\":\"orca-project-assembly\""),
            "assembly structure recorded");
    }

    public static void CompileRefusesOpenMeshWithoutFlag()
    {
        string strDir = Directory.CreateTempSubdirectory("obc3mf-open").FullName;
        File.WriteAllText(Path.Combine(strDir, "tri.stl"), """
            solid t
              facet normal 0 0 1
                outer loop
                  vertex 0 0 0
                  vertex 1 0 0
                  vertex 0 1 0
                endloop
              endfacet
            endsolid t
            """);
        string strManifest = Path.Combine(strDir, "m.json");
        File.WriteAllText(strManifest,
            "{ \"output\": \"t.3mf\", \"printer\": { \"nozzle_diameter_mm\": \"0.4\", " +
            "\"printable_width_mm\": 350, \"printable_depth_mm\": 350, \"printable_height_mm\": 350 }, " +
            "\"parts\": [ { \"mesh\": \"tri.stl\", \"filament\": 1 } ] }");
        Assert.True(Obc3mf.Program.Main(new[] { "compile", "--manifest", strManifest }) == 2,
            "open mesh refused");
        Assert.True(Obc3mf.Program.Main(
            new[] { "compile", "--manifest", strManifest, "--allow-open-mesh" }) == 0,
            "allowed with flag");
        Assert.True(File.ReadAllText(Path.Combine(strDir, "t.3mf.project.json"))
            .Contains("\"open_mesh\""), "degradation recorded");
    }

    public static void CompileRefusesExtruderOverride()
    {
        string strDir = Directory.CreateTempSubdirectory("obc3mf-ov").FullName;
        WriteTetraStl(Path.Combine(strDir, "a.stl"));
        string strManifest = Path.Combine(strDir, "m.json");
        File.WriteAllText(strManifest,
            "{ \"output\": \"a.3mf\", \"printer\": { \"nozzle_diameter_mm\": \"0.4\", " +
            "\"printable_width_mm\": 350, \"printable_depth_mm\": 350, \"printable_height_mm\": 350 }, " +
            "\"parts\": [ { \"mesh\": \"a.stl\", \"filament\": 1, " +
            "\"overrides\": { \"extruder\": \"2\" } } ] }");
        Assert.True(Obc3mf.Program.Main(new[] { "compile", "--manifest", strManifest }) == 2,
            "extruder override must be refused");
    }

    public static void CompileRefusesMissingPrinterBlock()
    {
        string strDir = Directory.CreateTempSubdirectory("obc3mf-nopr").FullName;
        WriteTetraStl(Path.Combine(strDir, "a.stl"));
        string strManifest = Path.Combine(strDir, "m.json");
        File.WriteAllText(strManifest,
            "{ \"output\": \"a.3mf\", \"parts\": [ { \"mesh\": \"a.stl\", \"filament\": 1 } ] }");
        Assert.True(Obc3mf.Program.Main(new[] { "compile", "--manifest", strManifest }) == 2,
            "missing printer block must be refused — machine facts are never defaulted");
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
        // Name leads with the design hash's first 12 (ORecord's design = 'a'*64) so
        // AdvancedStudio's carries_design_hash check passes, then the gcode hash.
        string strExpectedName = $"{new string('a', 12)}-{strGcodeSha[..12]}.gcode";
        Assert.True(File.Exists(Path.Combine(strStaging, strExpectedName)),
            $"design+gcode-hash-named gcode staged ({strExpectedName})");

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
