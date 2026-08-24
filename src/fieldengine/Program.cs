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
        fieldengine make-vdb sphere --center X,Y,Z --radius R --voxel V --out FILE
        fieldengine make-vdb box    --min X,Y,Z --max X,Y,Z --voxel V --out FILE

        Spec (obc-field/0.2): geometry (mesh, units "mm"), voxel_size_mm (explicit,
        never defaulted), field: axis_bands { axis, bands:[{filament, up_to_mm,
        overrides?}] } or vdb_regions { regions:[{vdb, filament, overrides?}],
        remainder?:{filament} }, printer (passed to the obc3mf manifest).
        Outputs: region_<n>_f<filament>.stl, manifest.json (obc3mf), field.json
        (canonical record, deterministic, timestamp-free).
        make-vdb authors OpenVDB material volumes without OpenVCAD (mm, explicit voxel).
        Exit codes: 0 ok · 2 invalid spec/mesh/arguments.
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
        if (args[0] == "make-vdb")
            return NMakeVdb(args);
        if (args[0] != "compile")
            throw new ArgumentException($"Unknown command '{args[0]}'. Commands: compile, make-vdb.");

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

    private static int NMakeVdb(string[] args)
    {
        if (args.Length < 2 || args[1] is not ("sphere" or "box"))
            throw new ArgumentException("make-vdb needs a shape: sphere | box.");
        string strShape = args[1];
        string? strOut = null;
        float fVoxel = 0;
        System.Numerics.Vector3? vecCenter = null, vecMin = null, vecMax = null;
        float fRadius = 0;

        static System.Numerics.Vector3 VecParse(string strValue, string strFlag)
        {
            string[] astr = strValue.Split(',');
            if (astr.Length != 3)
                throw new ArgumentException($"{strFlag} expects X,Y,Z in mm.");
            return new System.Numerics.Vector3(
                float.Parse(astr[0], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(astr[1], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(astr[2], System.Globalization.CultureInfo.InvariantCulture));
        }

        for (int i = 2; i < args.Length; i++)
        {
            string StrNext() => i + 1 < args.Length ? args[++i]
                : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--out": strOut = StrNext(); break;
                case "--voxel":
                    fVoxel = float.Parse(StrNext(), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--center": vecCenter = VecParse(StrNext(), "--center"); break;
                case "--radius":
                    fRadius = float.Parse(StrNext(), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--min": vecMin = VecParse(StrNext(), "--min"); break;
                case "--max": vecMax = VecParse(StrNext(), "--max"); break;
                default: throw new ArgumentException($"Unknown argument '{args[i]}'.");
            }
        }
        if (strOut is null || fVoxel <= 0)
            throw new ArgumentException("make-vdb needs --out and a positive --voxel (mm; explicit, never defaulted).");

        using PicoGK.Library oLib = new(fVoxel);
        PicoGK.Voxels vox;
        if (strShape == "sphere")
        {
            if (vecCenter is null || fRadius <= 0)
                throw new ArgumentException("sphere needs --center X,Y,Z and a positive --radius (mm).");
            // Library-taking overloads throughout: the short ones assume Library.Go
            // ambient mode and throw under instance mode.
            vox = PicoGK.Voxels.voxSphere(oLib, vecCenter.Value, fRadius);
        }
        else
        {
            if (vecMin is null || vecMax is null)
                throw new ArgumentException("box needs --min X,Y,Z and --max X,Y,Z (mm).");
            vox = new PicoGK.Voxels(PicoGK.Utils.mshCreateCube(
                oLib, new PicoGK.BBox3(vecMin.Value, vecMax.Value)));
        }
        vox.SaveToVdbFile(strOut);
        Console.WriteLine($"vdb: {strOut}");
        Console.WriteLine($"sha256: {SliceRecord.StrHashFile(strOut)}");
        return 0;
    }
}
