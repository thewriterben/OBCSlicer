using System.Globalization;

namespace ObcSlice;

/// <summary>
/// obcslice — provenance-carrying headless slice wrapper around OrcaSlicer_AS.
/// Thin CLI over <see cref="SliceOperation"/>; the MCP server is the other surface.
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

        if (strModel is null || strMachine is null || strProcess is null || strOutDir is null)
            throw new ArgumentException("--model, --machine, --process and --outdir are required.");

        SliceOperation.Outcome oOutcome = SliceOperation.Execute(new SliceOperation.Request
        {
            StrModel = strModel,
            StrMachine = strMachine,
            StrProcess = strProcess,
            AstrFilaments = astrFilaments,
            StrOutDir = strOutDir,
            StrSlicer = strSlicer,
            StrDesignSidecar = strSidecar,
            StrDesignRunId = strRunId,
            StrSlicerCommit = strSlicerCommit,
            NPlate = nPlate,
            Overrides = oOverrides,
            BDryRun = bDryRun,
        });

        Console.WriteLine($"slice record: {oOutcome.StrRecordPath}");
        Console.WriteLine($"record sha256: {oOutcome.StrRecordSha256}");
        foreach (Degradation oD in oOutcome.Degradations)
            Console.WriteLine($"degradation: {oD.StrCode} — {oD.StrDetail}");
        foreach (SliceRecord.OutputRef oO in oOutcome.Outputs)
            Console.WriteLine($"output: {oO.StrName} sha256={oO.StrSha256}");

        return oOutcome.NExitCode == 0 ? 0 : 1;
    }
}
