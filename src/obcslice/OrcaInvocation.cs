using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ObcSlice;

/// <summary>Builds and runs the OrcaSlicer_AS headless invocation per
/// docs/specs/ORCA-CLI.md. Pure argv construction is separated from execution
/// so tests cover it without a slicer binary.</summary>
public static class OrcaInvocation
{
    /// <summary>Argv (excluding argv[0]) for a plate slice. Deterministic for given inputs.</summary>
    public static List<string> BuildArgs(
        string strModelPath,
        string strMachineProfile,
        string strProcessProfile,
        IReadOnlyList<string> astrFilamentProfiles,
        string strOutDir,
        int nPlate,
        IReadOnlyDictionary<string, string> oOverrides)
    {
        var astrArgs = new List<string>
        {
            strModelPath,
            "--load-settings", string.Join(';', strMachineProfile, strProcessProfile),
        };
        if (astrFilamentProfiles.Count > 0)
        {
            astrArgs.Add("--load-filaments");
            astrArgs.Add(string.Join(';', astrFilamentProfiles));
        }
        astrArgs.Add("--slice");
        astrArgs.Add(nPlate.ToString());
        astrArgs.Add("--outputdir");
        astrArgs.Add(strOutDir);
        foreach (KeyValuePair<string, string> oKv in oOverrides.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            astrArgs.Add($"--{oKv.Key}");
            astrArgs.Add(oKv.Value);
        }
        return astrArgs;
    }

    /// <summary>Detects the unflattened-profile hazard (ORCA-CLI.md caveat 1).</summary>
    public static bool BProfileHasInherits(string strProfilePath)
    {
        try
        {
            using JsonDocument oDoc = JsonDocument.Parse(File.ReadAllText(strProfilePath));
            return oDoc.RootElement.ValueKind == JsonValueKind.Object
                && oDoc.RootElement.TryGetProperty("inherits", out JsonElement oInherits)
                && oInherits.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(oInherits.GetString());
        }
        catch (JsonException)
        {
            // Not JSON at all — the slicer will fail loudly; not this check's job.
            return false;
        }
    }

    public sealed record RunResult(int NExitCode, string StrStderrTail);

    public static RunResult Run(string strSlicerBinary, IReadOnlyList<string> astrArgs)
    {
        var oPsi = new ProcessStartInfo
        {
            FileName = strSlicerBinary,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string strArg in astrArgs)
            oPsi.ArgumentList.Add(strArg);

        using Process oProcess = Process.Start(oPsi)
            ?? throw new InvalidOperationException($"Failed to start slicer: {strSlicerBinary}");
        // Drain both pipes; keep only a bounded stderr tail for the record.
        StringBuilder oStderr = new();
        oProcess.ErrorDataReceived += (_, e) => { if (e.Data is not null) oStderr.AppendLine(e.Data); };
        oProcess.BeginErrorReadLine();
        oProcess.StandardOutput.ReadToEnd();
        oProcess.WaitForExit();

        string strTail = oStderr.ToString();
        if (strTail.Length > 2000)
            strTail = strTail[^2000..];
        return new RunResult(oProcess.ExitCode, strTail);
    }
}
