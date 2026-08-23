using ObcSlice;

namespace FieldEngine;

/// <summary>
/// fieldengine — compile a material field over a mesh into discrete per-material
/// regions + an obc3mf manifest. The OpenVCAD-shaped step of the pipeline,
/// implemented clean-room on PicoGK/OpenVDB (ADR-0003). See docs/specs/FIELD-SPEC.md.
/// </summary>
public static class Program
{
    private const string StrUsage = """
        fieldengine compile --spec FILE --outdir DIR

        Spec (obc-field/0.1): geometry (mesh, units "mm"), voxel_size_mm (explicit,
        never defaulted), field { type: "axis_bands", axis, bands: [ { filament,
        up_to_mm }... ] }, printer (passed to the obc3mf manifest).
        Outputs: region_<band>_f<filament>.stl per band, manifest.json (obc3mf),
        field.json (canonical record, deterministic, timestamp-free).
        Exit codes: 0 ok · 2 invalid spec/mesh.
        """;

    public static int Main(string[] args)
    {
        try
        {
            return MainInner(args);
        }
        catch (Exception e) when (e is InvalidDataException or FileNotFoundException or ArgumentException)
        {
            Console.Error.WriteLine($"fieldengine: {e.Message}");
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

        string? strSpec = null, strOutDir = null;
        for (int i = 1; i < args.Length; i++)
        {
            string StrNext() => i + 1 < args.Length ? args[++i]
                : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--spec": strSpec = StrNext(); break;
                case "--outdir": strOutDir = StrNext(); break;
                default: throw new ArgumentException($"Unknown argument '{args[i]}'.");
            }
        }
        if (strSpec is null || strOutDir is null)
            throw new ArgumentException("--spec and --outdir are required.");

        FieldSpec oSpec = FieldSpec.OParse(strSpec);
        CompileRun.Outcome oOutcome = CompileRun.Execute(oSpec, strOutDir);

        Console.WriteLine($"field record: {oOutcome.StrRecordPath}");
        Console.WriteLine($"record sha256: {SliceRecord.StrHashFile(oOutcome.StrRecordPath)}");
        foreach (string strRegion in oOutcome.RegionPaths)
            Console.WriteLine($"region: {strRegion}");
        foreach (Degradation oD in oOutcome.Degradations)
            Console.WriteLine($"degradation: {oD.StrCode} — {oD.StrDetail}");
        return 0;
    }
}
