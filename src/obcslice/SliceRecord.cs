using System.Security.Cryptography;
using System.Text.Json;

namespace ObcSlice;

/// <summary>One degradation entry. Never silent: everything that weakens the
/// attestation lands in the record's `degradations` array (SLICE-RECORD.md rule 3).</summary>
public sealed record Degradation(string StrCode, string StrDetail);

/// <summary>Immutable description of a slicing run, buildable without executing
/// anything (dry runs are records too). Serializes to canonical JSON per
/// docs/specs/SLICE-RECORD.md, schema `obc-slice/0.1`.</summary>
public sealed class SliceRecord
{
    public const string StrSchema = "obc-slice/0.1";
    public const string StrMediaType = "application/vnd.obc.slice+json";

    public required string StrCreatedUtc { get; init; }          // ISO-8601 Z, seconds
    public required string StrModelName { get; init; }
    public required string StrModelSha256 { get; init; }
    public required string StrModelMediaType { get; init; }
    public required IReadOnlyList<ProfileRef> Profiles { get; init; }
    public required IReadOnlyDictionary<string, string> Overrides { get; init; }
    public DesignProvenance? Design { get; init; }
    public required string StrSlicerName { get; init; }
    public required string StrSlicerBinarySha256 { get; init; }
    public required string StrSlicerCommit { get; init; }
    public required IReadOnlyList<string> Invocation { get; init; }
    public required int NExitCode { get; init; }
    public required IReadOnlyList<OutputRef> Outputs { get; init; }
    public required string StrStderrTail { get; init; }
    public required IReadOnlyList<Degradation> Degradations { get; init; }

    public sealed record ProfileRef(string StrRole, string StrName, string StrSha256, int? NSlot);
    public sealed record OutputRef(string StrName, string StrSha256, int NPlate);
    public sealed record DesignProvenance(
        string StrSchema, string StrSidecarSha256, string StrArtifactSha256, string StrRunId);

    /// <summary>Canonical bytes — this is the file content; its SHA-256 is the record's identity.</summary>
    public byte[] ToCanonicalBytes()
    {
        var oProfiles = new List<object?>();
        foreach (ProfileRef oP in Profiles)
        {
            var oEntry = new Dictionary<string, object?>
            {
                ["role"] = oP.StrRole, ["name"] = oP.StrName, ["sha256"] = oP.StrSha256,
            };
            if (oP.NSlot is int nSlot)
                oEntry["slot"] = nSlot;
            oProfiles.Add(oEntry);
        }

        var oOutputs = new List<object?>();
        foreach (OutputRef oO in Outputs)
            oOutputs.Add(new Dictionary<string, object?>
            {
                ["name"] = oO.StrName, ["sha256"] = oO.StrSha256, ["plate"] = oO.NPlate,
            });

        var oDegradations = new List<object?>();
        foreach (Degradation oD in Degradations)
            oDegradations.Add(new Dictionary<string, object?>
            {
                ["code"] = oD.StrCode, ["detail"] = oD.StrDetail,
            });

        object? oDesign = Design is null ? null : new Dictionary<string, object?>
        {
            ["system"] = "opendesigncore",
            ["schema"] = Design.StrSchema,
            ["sidecar_sha256"] = Design.StrSidecarSha256,
            ["artifact_sha256"] = Design.StrArtifactSha256,
            ["run_id"] = Design.StrRunId,
        };

        var oRecord = new Dictionary<string, object?>
        {
            ["record"] = StrSchema,
            ["created_utc"] = StrCreatedUtc,
            ["inputs"] = new Dictionary<string, object?>
            {
                ["model"] = new Dictionary<string, object?>
                {
                    ["name"] = StrModelName,
                    ["sha256"] = StrModelSha256,
                    ["media_type"] = StrModelMediaType,
                },
                ["design_provenance"] = oDesign,
                ["profiles"] = oProfiles,
                ["overrides"] = Overrides.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
            },
            ["slicer"] = new Dictionary<string, object?>
            {
                ["name"] = StrSlicerName,
                ["binary_sha256"] = StrSlicerBinarySha256,
                ["commit"] = StrSlicerCommit,
                ["invocation"] = Invocation.Cast<object?>().ToList(),
            },
            ["result"] = new Dictionary<string, object?>
            {
                ["exit_code"] = NExitCode,
                ["outputs"] = oOutputs,
                ["stderr_tail"] = StrStderrTail,
            },
            ["degradations"] = oDegradations,
            ["determinism"] = "attested",
        };
        return CanonicalJson.Serialize(oRecord);
    }

    public static string StrHashFile(string strPath)
    {
        using FileStream oStream = File.OpenRead(strPath);
        return Convert.ToHexString(SHA256.HashData(oStream)).ToLowerInvariant();
    }

    /// <summary>Parse an ODC provenance sidecar and verify its artifact hash matches the
    /// model file. Mismatch is a boundary-validation failure: throw, do not degrade.</summary>
    public static DesignProvenance ParseAndVerifySidecar(
        string strSidecarPath, string strModelSha256, string strRunId)
    {
        using JsonDocument oDoc = JsonDocument.Parse(File.ReadAllText(strSidecarPath));
        JsonElement oRoot = oDoc.RootElement;

        string strSchema = oRoot.TryGetProperty("schema", out JsonElement oSchema)
            ? oSchema.GetString() ?? "unknown" : "unknown";
        if (!oRoot.TryGetProperty("artifact", out JsonElement oArtifact) ||
            !oArtifact.TryGetProperty("sha256", out JsonElement oSha))
            throw new InvalidDataException(
                $"Design sidecar {strSidecarPath} has no artifact.sha256 — not an ODC provenance sidecar.");

        string strArtifactSha = oSha.GetString()
            ?? throw new InvalidDataException("artifact.sha256 is not a string.");
        if (!string.Equals(strArtifactSha, strModelSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Design sidecar does not describe this model file: " +
                $"sidecar artifact.sha256={strArtifactSha} but sha256(model)={strModelSha256}. " +
                "Refusing to slice — the provenance chain must not start with a lie.");

        return new DesignProvenance(
            strSchema, StrHashFile(strSidecarPath), strArtifactSha.ToLowerInvariant(), strRunId);
    }
}
