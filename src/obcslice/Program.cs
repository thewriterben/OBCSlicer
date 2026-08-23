using System.Globalization;

namespace ObcSlice;

/// <summary>
/// obcslice — provenance-carrying headless slice wrapper around OrcaSlicer_AS.
/// Mesh + profiles (+ optional ODC design sidecar) in → G-code + slice record out.
/// See docs/specs/SLICE-RECORD.md and docs/specs/ORCA-CLI.md.
/// </summary>
public static class Program
{
    private const string StrUsage = """
        obcslice slice --model FILE --machine JSON --process JSON [--filament JSON]...
                       --outdir DIR [--slicer EXE | env OBCSLICE_ORCA]
                       [--design-sidecar JSON] [--design-run-id ID] [--slicer-commit SHA]
                       [--plate N] [--set key=value]... [--dry-run]

        Slices headlessly via OrcaSlicer_AS and writes <model-stem>.slice.json
        (canonical JSON, schema obc-slice/0.1) next to the G-code.
        Exit codes: 0 ok · 1 slice failed (record still written) · 2 invalid arguments/inputs.
        """;

    private static readonly Dictionary<string, string> MediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".stl"] = "model/stl",
        [".3mf"] = "model/3mf",
        [".step"] = "model/step",
        [".stp"] = "model/step",
        [".obj"] = "model/obj",
    };

    public static int Main(string[] args)
    {
        try
        {
            return MainInner(args);
        }
        catch (Exception e) when (e is InvalidDataException or FileNotFoundException or ArgumentException)
        {
            Console.Error.WriteLine($"obcslice: {e.Message}");
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
        if (args[0] != "slice")
            throw new ArgumentException($"Unknown command '{args[0]}'. Only 'slice' exists in v0.1.");

        // ---- parse ------------------------------------------------------------
        string? strModel = null, strMachine = null, strProcess = null, strOutDir = null;
        string? strSlicer = Environment.GetEnvironmentVariable("OBCSLICE_ORCA");
        string? strSidecar = null, strSlicerCommit = null;
        string strRunId = "unknown";
        int nPlate = 0;
        bool bDryRun = false;
        List<string> astrFilaments = new();
        Dictionary<string, string> oOverrides = new(StringComparer.Ordinal);

        for (int i = 1; i < args.Length; i++)
        {
            string StrNext(string strFlag) => i + 1 < args.Length
                ? args[++i]
                : throw new ArgumentException($"{strFlag} needs a value.");
            switch (args[i])
            {
                case "--model": strModel = StrNext("--model"); break;
                case "--machine": strMachine = StrNext("--machine"); break;
                case "--process": strProcess = StrNext("--process"); break;
                case "--filament": astrFilaments.Add(StrNext("--filament")); break;
                case "--outdir": strOutDir = StrNext("--outdir"); break;
                case "--slicer": strSlicer = StrNext("--slicer"); break;
                case "--design-sidecar": strSidecar = StrNext("--design-sidecar"); break;
                case "--design-run-id": strRunId = StrNext("--design-run-id"); break;
                case "--slicer-commit": strSlicerCommit = StrNext("--slicer-commit"); break;
                case "--plate":
                    nPlate = int.Parse(StrNext("--plate"), CultureInfo.InvariantCulture);
                    break;
                case "--set":
                    string strPair = StrNext("--set");
                    int nEq = strPair.IndexOf('=');
                    if (nEq <= 0)
                        throw new ArgumentException($"--set expects key=value, got '{strPair}'.");
                    oOverrides[strPair[..nEq]] = strPair[(nEq + 1)..];
                    break;
                case "--dry-run": bDryRun = true; break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[i]}'.");
            }
        }

        // ---- validate at the boundary ----------------------------------------
        if (strModel is null || strMachine is null || strProcess is null || strOutDir is null)
            throw new ArgumentException("--model, --machine, --process and --outdir are required.");
        foreach (string strPath in new[] { strModel, strMachine, strProcess }.Concat(astrFilaments))
            if (!File.Exists(strPath))
                throw new FileNotFoundException($"Input not found: {strPath}");
        if (strSidecar is not null && !File.Exists(strSidecar))
            throw new FileNotFoundException($"Design sidecar not found: {strSidecar}");
        if (!bDryRun && (strSlicer is null || !File.Exists(strSlicer)))
            throw new ArgumentException(
                "No slicer binary: pass --slicer or set OBCSLICE_ORCA (or use --dry-run).");

        string strExt = Path.GetExtension(strModel);
        if (!MediaTypes.TryGetValue(strExt, out string? strMediaType))
            throw new ArgumentException(
                $"Unsupported model type '{strExt}'. Supported: {string.Join(", ", MediaTypes.Keys)}.");

        Directory.CreateDirectory(strOutDir);

        // ---- hash inputs, verify design linkage ------------------------------
        List<Degradation> aDegradations = new();
        string strModelSha = SliceRecord.StrHashFile(strModel);

        SliceRecord.DesignProvenance? oDesign = null;
        if (strSidecar is not null)
            oDesign = SliceRecord.ParseAndVerifySidecar(strSidecar, strModelSha, strRunId); // throws on mismatch
        else
            aDegradations.Add(new Degradation("no_design_provenance",
                "No --design-sidecar given; this slice is not linked to a design run."));

        var aProfiles = new List<SliceRecord.ProfileRef>
        {
            new("machine", Path.GetFileName(strMachine), SliceRecord.StrHashFile(strMachine), null),
            new("process", Path.GetFileName(strProcess), SliceRecord.StrHashFile(strProcess), null),
        };
        for (int i = 0; i < astrFilaments.Count; i++)
            aProfiles.Add(new("filament", Path.GetFileName(astrFilaments[i]),
                SliceRecord.StrHashFile(astrFilaments[i]), i + 1));

        foreach (string strProfile in new[] { strMachine, strProcess }.Concat(astrFilaments))
            if (OrcaInvocation.BProfileHasInherits(strProfile))
                aDegradations.Add(new Degradation("unflattened_profile",
                    $"{Path.GetFileName(strProfile)} contains 'inherits'; " +
                    "export a flattened preset for a stable attestation (ORCA-CLI.md caveat 1)."));

        if (strSlicerCommit is null)
            aDegradations.Add(new Degradation("slicer_commit_unrecorded",
                "No --slicer-commit given; binary hash still identifies the slicer."));

        // ---- run --------------------------------------------------------------
        List<string> astrArgs = OrcaInvocation.BuildArgs(
            strModel, strMachine, strProcess, astrFilaments, strOutDir, nPlate, oOverrides);

        int nExitCode;
        string strStderrTail;
        string strBinarySha;
        if (bDryRun)
        {
            aDegradations.Add(new Degradation("dry_run", "Slicer not executed; no outputs."));
            nExitCode = 0;
            strStderrTail = "";
            strBinarySha = strSlicer is not null && File.Exists(strSlicer)
                ? SliceRecord.StrHashFile(strSlicer) : "unknown";
        }
        else
        {
            strBinarySha = SliceRecord.StrHashFile(strSlicer!);
            OrcaInvocation.RunResult oRun = OrcaInvocation.Run(
                strSlicer!, astrArgs);
            nExitCode = oRun.NExitCode;
            strStderrTail = oRun.StrStderrTail;
        }

        // ---- collect outputs --------------------------------------------------
        var aOutputs = new List<SliceRecord.OutputRef>();
        if (!bDryRun)
        {
            foreach (string strGcode in Directory.EnumerateFiles(strOutDir, "plate_*.gcode")
                         .OrderBy(s => s, StringComparer.Ordinal))
            {
                string strName = Path.GetFileName(strGcode);
                string strPlatePart = strName["plate_".Length..].Split('_', '.')[0];
                int nPlateNum = int.TryParse(strPlatePart, NumberStyles.None,
                    CultureInfo.InvariantCulture, out int n) ? n : 0;
                aOutputs.Add(new(strName, SliceRecord.StrHashFile(strGcode), nPlateNum));
            }
            if (nExitCode != 0)
                aDegradations.Add(new Degradation("slice_failed",
                    $"Slicer exit code {nExitCode}; do not propose uploads from this record."));
            else if (aOutputs.Count == 0)
                aDegradations.Add(new Degradation("output_missing",
                    "Slicer exited 0 but produced no plate_*.gcode in the output directory."));
        }

        // ---- write the record -------------------------------------------------
        var oRecord = new SliceRecord
        {
            StrCreatedUtc = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            StrModelName = Path.GetFileName(strModel),
            StrModelSha256 = strModelSha,
            StrModelMediaType = strMediaType,
            Profiles = aProfiles,
            Overrides = oOverrides,
            Design = oDesign,
            StrSlicerName = "OrcaSlicer_AS",
            StrSlicerBinarySha256 = strBinarySha,
            StrSlicerCommit = strSlicerCommit ?? "unknown",
            Invocation = astrArgs.Prepend(strSlicer ?? "orca-slicer").ToList(),
            NExitCode = nExitCode,
            Outputs = aOutputs,
            StrStderrTail = strStderrTail,
            Degradations = aDegradations,
        };

        byte[] abRecord = oRecord.ToCanonicalBytes();
        string strRecordPath = Path.Combine(strOutDir,
            Path.GetFileNameWithoutExtension(strModel) + ".slice.json");
        File.WriteAllBytes(strRecordPath, abRecord);

        Console.WriteLine($"slice record: {strRecordPath}");
        Console.WriteLine($"record sha256: {SliceRecord.StrHashFile(strRecordPath)}");
        foreach (Degradation oD in aDegradations)
            Console.WriteLine($"degradation: {oD.StrCode} — {oD.StrDetail}");
        foreach (SliceRecord.OutputRef oO in aOutputs)
            Console.WriteLine($"output: {oO.StrName} sha256={oO.StrSha256}");

        return nExitCode == 0 ? 0 : 1;
    }
}
