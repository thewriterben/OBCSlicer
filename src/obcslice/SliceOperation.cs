using System.Globalization;

namespace ObcSlice;

/// <summary>The slice operation itself, callable from the CLI (Program) and the
/// MCP server without going through argv. One code path, two surfaces.</summary>
public static class SliceOperation
{
    public sealed record Request
    {
        public required string StrModel { get; init; }
        public required string StrMachine { get; init; }
        public required string StrProcess { get; init; }
        public IReadOnlyList<string> AstrFilaments { get; init; } = Array.Empty<string>();
        public required string StrOutDir { get; init; }
        public string? StrSlicer { get; init; }
        public string? StrDesignSidecar { get; init; }
        public string StrDesignRunId { get; init; } = "unknown";
        public string? StrSlicerCommit { get; init; }
        public int NPlate { get; init; }
        public IReadOnlyDictionary<string, string> Overrides { get; init; }
            = new Dictionary<string, string>();
        public bool BDryRun { get; init; }
    }

    public sealed record Outcome(
        string StrRecordPath,
        string StrRecordSha256,
        int NExitCode,
        IReadOnlyList<SliceRecord.OutputRef> Outputs,
        IReadOnlyList<Degradation> Degradations);

    private static readonly Dictionary<string, string> MediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".stl"] = "model/stl",
        [".3mf"] = "model/3mf",
        [".step"] = "model/step",
        [".stp"] = "model/step",
        [".obj"] = "model/obj",
    };

    /// <summary>Validate at the boundary, slice (unless dry run), write the record.
    /// Throws ArgumentException / FileNotFoundException / InvalidDataException on
    /// invalid inputs — including a design sidecar that does not match the model.</summary>
    public static Outcome Execute(Request oReq)
    {
        foreach (string strPath in new[] { oReq.StrModel, oReq.StrMachine, oReq.StrProcess }
                     .Concat(oReq.AstrFilaments))
            if (!File.Exists(strPath))
                throw new FileNotFoundException($"Input not found: {strPath}");
        if (oReq.StrDesignSidecar is not null && !File.Exists(oReq.StrDesignSidecar))
            throw new FileNotFoundException($"Design sidecar not found: {oReq.StrDesignSidecar}");
        if (!oReq.BDryRun && (oReq.StrSlicer is null || !File.Exists(oReq.StrSlicer)))
            throw new ArgumentException(
                "No slicer binary: pass --slicer or set OBCSLICE_ORCA (or use --dry-run).");

        string strExt = Path.GetExtension(oReq.StrModel);
        if (!MediaTypes.TryGetValue(strExt, out string? strMediaType))
            throw new ArgumentException(
                $"Unsupported model type '{strExt}'. Supported: {string.Join(", ", MediaTypes.Keys)}.");

        Directory.CreateDirectory(oReq.StrOutDir);

        List<Degradation> aDegradations = new();
        string strModelSha = SliceRecord.StrHashFile(oReq.StrModel);

        SliceRecord.DesignProvenance? oDesign = null;
        if (oReq.StrDesignSidecar is not null)
            oDesign = SliceRecord.ParseAndVerifySidecar(
                oReq.StrDesignSidecar, strModelSha, oReq.StrDesignRunId); // throws on mismatch
        else
            aDegradations.Add(new Degradation("no_design_provenance",
                "No design sidecar given; this slice is not linked to a design run."));

        var aProfiles = new List<SliceRecord.ProfileRef>
        {
            new("machine", Path.GetFileName(oReq.StrMachine), SliceRecord.StrHashFile(oReq.StrMachine), null),
            new("process", Path.GetFileName(oReq.StrProcess), SliceRecord.StrHashFile(oReq.StrProcess), null),
        };
        for (int i = 0; i < oReq.AstrFilaments.Count; i++)
            aProfiles.Add(new("filament", Path.GetFileName(oReq.AstrFilaments[i]),
                SliceRecord.StrHashFile(oReq.AstrFilaments[i]), i + 1));

        foreach (string strProfile in new[] { oReq.StrMachine, oReq.StrProcess }.Concat(oReq.AstrFilaments))
            if (OrcaInvocation.BProfileHasInherits(strProfile))
                aDegradations.Add(new Degradation("unflattened_profile",
                    $"{Path.GetFileName(strProfile)} contains 'inherits'; " +
                    "export a flattened preset for a stable attestation (ORCA-CLI.md caveat 1)."));

        if (oReq.StrSlicerCommit is null)
            aDegradations.Add(new Degradation("slicer_commit_unrecorded",
                "No slicer commit given; binary hash still identifies the slicer."));

        List<string> astrArgs = OrcaInvocation.BuildArgs(
            oReq.StrModel, oReq.StrMachine, oReq.StrProcess, oReq.AstrFilaments,
            oReq.StrOutDir, oReq.NPlate, oReq.Overrides);

        int nExitCode;
        string strStderrTail;
        string strBinarySha;
        if (oReq.BDryRun)
        {
            aDegradations.Add(new Degradation("dry_run", "Slicer not executed; no outputs."));
            nExitCode = 0;
            strStderrTail = "";
            strBinarySha = oReq.StrSlicer is not null && File.Exists(oReq.StrSlicer)
                ? SliceRecord.StrHashFile(oReq.StrSlicer) : "unknown";
        }
        else
        {
            strBinarySha = SliceRecord.StrHashFile(oReq.StrSlicer!);
            OrcaInvocation.RunResult oRun = OrcaInvocation.Run(oReq.StrSlicer!, astrArgs);
            nExitCode = oRun.NExitCode;
            strStderrTail = oRun.StrStderrTail;
        }

        var aOutputs = new List<SliceRecord.OutputRef>();
        if (!oReq.BDryRun)
        {
            foreach (string strGcode in Directory.EnumerateFiles(oReq.StrOutDir, "plate_*.gcode")
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

        var oRecord = new SliceRecord
        {
            StrCreatedUtc = DateTime.UtcNow.ToString(
                "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            StrModelName = Path.GetFileName(oReq.StrModel),
            StrModelSha256 = strModelSha,
            StrModelMediaType = strMediaType,
            Profiles = aProfiles,
            Overrides = oReq.Overrides,
            Design = oDesign,
            StrSlicerName = "OrcaSlicer_AS",
            StrSlicerBinarySha256 = strBinarySha,
            StrSlicerCommit = oReq.StrSlicerCommit ?? "unknown",
            Invocation = astrArgs.Prepend(oReq.StrSlicer ?? "orca-slicer").ToList(),
            NExitCode = nExitCode,
            Outputs = aOutputs,
            StrStderrTail = strStderrTail,
            Degradations = aDegradations,
        };

        byte[] abRecord = oRecord.ToCanonicalBytes();
        string strRecordPath = Path.Combine(oReq.StrOutDir,
            Path.GetFileNameWithoutExtension(oReq.StrModel) + ".slice.json");
        File.WriteAllBytes(strRecordPath, abRecord);

        return new Outcome(strRecordPath, SliceRecord.StrHashFile(strRecordPath),
            nExitCode, aOutputs, aDegradations);
    }
}
