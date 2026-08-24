using System.Globalization;
using System.Numerics;
using ObcSlice;
using PicoGK;

namespace FieldEngine;

/// <summary>Field compile: mesh + material field → per-material region meshes
/// (watertight by construction — voxel-derived) + obc3mf assembly manifest +
/// deterministic field record. Two field types: axis_bands (slab booleans) and
/// vdb_regions (OpenVDB volumes in — the OpenVCAD/ODC interop seam).
/// Deliberately timestamp-free like ODC sidecars.</summary>
public static class CompileRun
{
    public const string StrToolVersion = "0.2.0";

    public sealed record Outcome(
        string StrRecordPath,
        IReadOnlyList<string> RegionPaths,
        IReadOnlyList<Degradation> Degradations);

    private sealed record Region(
        string StrLabel, int NFilament, IReadOnlyDictionary<string, string> Overrides, Voxels Vox);

    public static Outcome Execute(FieldSpec oSpec, string strOutDir)
    {
        Directory.CreateDirectory(strOutDir);
        var aDegradations = new List<Degradation>();
        string strMeshSha = SliceRecord.StrHashFile(oSpec.StrMeshPath);
        string strPicoGk = typeof(PicoGK.Library).Assembly.GetName().Version?.ToString() ?? "unknown";

        var aRegionFiles = new List<(string StrPath, string StrSha, int NFilament,
            IReadOnlyDictionary<string, string> Overrides, int NTriangles)>();
        var aInputVdbs = new List<object?>();

        using (Library oLib = new(oSpec.FVoxelSizeMm))
        {
            Mesh mshPart = Mesh.mshFromStlFile(
                oSpec.StrMeshPath, Mesh.EStlUnit.MM, 1.0f, null, oLib);
            if (mshPart.nTriangleCount() == 0)
                throw new InvalidDataException("Mesh has no triangles after import.");

            Voxels voxPart = new(mshPart);
            if (voxPart.bIsEmpty())
                throw new InvalidDataException(
                    "Mesh voxelized to an empty field — likely not watertight. " +
                    "Repair it (BumpMesh output and voxel-derived meshes are safe inputs).");

            List<Region> aRegions = oSpec.StrFieldType == "vdb_regions"
                ? ARegionsFromVdbs(oLib, oSpec, voxPart, aDegradations, aInputVdbs)
                : ARegionsFromBands(oLib, oSpec, voxPart, aDegradations);

            int nIndex = 0;
            foreach (Region oRegion in aRegions)
            {
                nIndex++;
                Mesh mshRegion = oRegion.Vox.mshAsMesh();
                if (mshRegion.nTriangleCount() == 0)
                    throw new InvalidDataException($"{oRegion.StrLabel}: meshing produced zero triangles.");
                string strRegionPath = Path.Combine(strOutDir,
                    $"region_{nIndex}_f{oRegion.NFilament}.stl");
                // Two-arg call like ODC's runs: the trailing (Vector3?, float) params
                // are offset/scale with sane defaults — passing 0 scale zeroes the mesh.
                mshRegion.SaveToStlFile(strRegionPath, Mesh.EStlUnit.MM);
                aRegionFiles.Add((strRegionPath, SliceRecord.StrHashFile(strRegionPath),
                    oRegion.NFilament, oRegion.Overrides, mshRegion.nTriangleCount()));
            }
        }

        if (aRegionFiles.Count == 0)
            throw new InvalidDataException("No region produced any volume — nothing to print.");
        if (aRegionFiles.Select(r => r.NFilament).Distinct().Count() < 2
            && aRegionFiles.All(r => r.Overrides.Count == 0))
            aDegradations.Add(new Degradation("single_material",
                "All surviving regions use one filament with no process overrides; " +
                "the result is effectively uniform."));

        // obc3mf manifest — assembly:true, regions overlap in XY by construction.
        var oManifest = new Dictionary<string, object?>
        {
            ["output"] = "field.3mf",
            ["assembly"] = true,
            ["printer"] = System.Text.Json.JsonSerializer.Deserialize<object>(oSpec.OPrinter.GetRawText()),
            ["parts"] = aRegionFiles.Select(r => (object?)new Dictionary<string, object?>
            {
                ["mesh"] = Path.GetFileName(r.StrPath),
                ["name"] = Path.GetFileNameWithoutExtension(r.StrPath),
                ["filament"] = r.NFilament,
                ["overrides"] = r.Overrides,
            }).ToList(),
        };
        string strManifestPath = Path.Combine(strOutDir, "manifest.json");
        File.WriteAllText(strManifestPath,
            System.Text.Json.JsonSerializer.Serialize(oManifest));

        // Field record — canonical, deterministic, timestamp-free (like ODC sidecars).
        object? oFieldBlock = oSpec.StrFieldType == "vdb_regions"
            ? new Dictionary<string, object?>
            {
                ["type"] = "vdb_regions",
                ["regions"] = aInputVdbs,
                ["remainder"] = oSpec.ORemainder is null ? null : new Dictionary<string, object?>
                {
                    ["filament"] = oSpec.ORemainder.NFilament,
                    ["overrides"] = oSpec.ORemainder.Overrides
                        .ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                },
            }
            : new Dictionary<string, object?>
            {
                ["type"] = "axis_bands",
                ["axis"] = oSpec.StrAxis,
                ["bands"] = oSpec.Bands.Select(b => (object?)new Dictionary<string, object?>
                {
                    ["filament"] = b.NFilament,
                    ["up_to_mm"] = b.FUpToMm is float f
                        ? f.ToString("F3", CultureInfo.InvariantCulture) : null,
                    ["overrides"] = b.Overrides.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                }).ToList(),
            };

        var oRecord = new Dictionary<string, object?>
        {
            ["record"] = "obc-field/0.2",
            ["units"] = "mm",
            ["voxel_size_mm"] = oSpec.FVoxelSizeMm.ToString("F3", CultureInfo.InvariantCulture),
            ["inputs"] = new Dictionary<string, object?>
            {
                ["mesh"] = Path.GetFileName(oSpec.StrMeshPath),
                ["mesh_sha256"] = strMeshSha,
                ["field"] = oFieldBlock,
            },
            ["outputs"] = aRegionFiles.Select(r => (object?)new Dictionary<string, object?>
            {
                ["name"] = Path.GetFileName(r.StrPath),
                ["sha256"] = r.StrSha,
                ["filament"] = r.NFilament,
                ["triangles"] = r.NTriangles,
            }).ToList(),
            ["manifest_sha256"] = SliceRecord.StrHashFile(strManifestPath),
            ["versions"] = new Dictionary<string, object?>
            {
                ["tool"] = StrToolVersion,
                ["picogk"] = strPicoGk,
            },
            ["degradations"] = aDegradations.Select(d => (object?)new Dictionary<string, object?>
            {
                ["code"] = d.StrCode, ["detail"] = d.StrDetail,
            }).ToList(),
        };
        string strRecordPath = Path.Combine(strOutDir, "field.json");
        File.WriteAllBytes(strRecordPath, CanonicalJson.Serialize(oRecord));

        return new Outcome(strRecordPath, aRegionFiles.Select(r => r.StrPath).ToList(), aDegradations);
    }

    private static List<Region> ARegionsFromBands(
        Library oLib, FieldSpec oSpec, Voxels voxPart, List<Degradation> aDegradations)
    {
        int nAxis = oSpec.StrAxis switch { "x" => 0, "y" => 1, _ => 2 };
        BBox3 oBox = voxPart.oCalculateBoundingBox();
        float fLoBound = (nAxis switch { 0 => oBox.vecMin.X, 1 => oBox.vecMin.Y, _ => oBox.vecMin.Z })
            - 2 * oSpec.FVoxelSizeMm;
        float fHiBound = (nAxis switch { 0 => oBox.vecMax.X, 1 => oBox.vecMax.Y, _ => oBox.vecMax.Z })
            + 2 * oSpec.FVoxelSizeMm;

        var aRegions = new List<Region>();
        float fLo = fLoBound;
        for (int i = 0; i < oSpec.Bands.Count; i++)
        {
            FieldSpec.Band oBand = oSpec.Bands[i];
            float fHi = oBand.FUpToMm ?? fHiBound;
            if (fHi <= fLo)
            {
                aDegradations.Add(new Degradation("empty_region",
                    $"Band {i + 1} (filament {oBand.NFilament}) lies entirely outside the part " +
                    $"along {oSpec.StrAxis}; no region emitted."));
                continue;
            }

            // Band slab as box voxels + boolean intersect — the pattern proven by
            // ODC's models. (voxIntersectImplicit's managed callback SEH-faults
            // under instance-Library mode in PicoGK 2.2.0 — verified live 2026-08-23.)
            Vector3 vecSlabMin = oBox.vecMin - new Vector3(2 * oSpec.FVoxelSizeMm);
            Vector3 vecSlabMax = oBox.vecMax + new Vector3(2 * oSpec.FVoxelSizeMm);
            switch (nAxis)
            {
                case 0: vecSlabMin.X = fLo; vecSlabMax.X = fHi; break;
                case 1: vecSlabMin.Y = fLo; vecSlabMax.Y = fHi; break;
                default: vecSlabMin.Z = fLo; vecSlabMax.Z = fHi; break;
            }
            Voxels voxSlab = new(Utils.mshCreateCube(oLib, new BBox3(vecSlabMin, vecSlabMax)));
            Voxels voxRegion = voxPart & voxSlab;
            fLo = fHi;

            if (voxRegion.bIsEmpty())
            {
                aDegradations.Add(new Degradation("empty_region",
                    $"Band {i + 1} (filament {oBand.NFilament}) intersects no part volume; " +
                    "no region emitted."));
                continue;
            }
            aRegions.Add(new Region($"Band {i + 1}", oBand.NFilament, oBand.Overrides, voxRegion));
        }
        return aRegions;
    }

    /// <summary>vdb_regions: region_i = (part ∩ vdb_i) − regions before it (priority
    /// order where volumes overlap). Part volume claimed by nothing goes to the
    /// explicit remainder — or the compile refuses. No silent material choices.</summary>
    private static List<Region> ARegionsFromVdbs(
        Library oLib, FieldSpec oSpec, Voxels voxPart, List<Degradation> aDegradations,
        List<object?> aInputVdbs)
    {
        var aRegions = new List<Region>();
        Voxels voxClaimed = new(oLib);
        bool bAnyClaimed = false;

        for (int i = 0; i < oSpec.VdbRegions.Count; i++)
        {
            FieldSpec.VdbRegion oR = oSpec.VdbRegions[i];
            aInputVdbs.Add(new Dictionary<string, object?>
            {
                ["vdb"] = Path.GetFileName(oR.StrVdbPath),
                ["vdb_sha256"] = SliceRecord.StrHashFile(oR.StrVdbPath),
                ["filament"] = oR.NFilament,
                ["overrides"] = oR.Overrides.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
            });

            Voxels voxVolume = Voxels.voxFromVdbFile(oLib, oR.StrVdbPath);
            if (voxVolume.bIsEmpty())
            {
                aDegradations.Add(new Degradation("empty_region",
                    $"Region {i + 1}: {Path.GetFileName(oR.StrVdbPath)} holds no volume."));
                continue;
            }

            Voxels voxRegion = voxPart & voxVolume;
            if (bAnyClaimed)
                voxRegion = voxRegion - voxClaimed;
            if (voxRegion.bIsEmpty())
            {
                aDegradations.Add(new Degradation("empty_region",
                    $"Region {i + 1} (filament {oR.NFilament}): no unclaimed part volume inside " +
                    $"{Path.GetFileName(oR.StrVdbPath)} — earlier regions have priority."));
                continue;
            }

            voxClaimed = bAnyClaimed ? voxClaimed + voxRegion : voxRegion.voxDuplicate();
            bAnyClaimed = true;
            aRegions.Add(new Region($"Region {i + 1}", oR.NFilament, oR.Overrides, voxRegion));
        }

        Voxels voxRemainder = bAnyClaimed ? voxPart - voxClaimed : voxPart;
        if (!voxRemainder.bIsEmpty())
        {
            if (oSpec.ORemainder is null)
                throw new InvalidDataException(
                    "The vdb regions do not cover the whole part and the spec names no " +
                    "\"remainder\" filament. Declare field.remainder — the engine will not " +
                    "silently assign leftover volume a material.");
            aRegions.Add(new Region("Remainder", oSpec.ORemainder.NFilament,
                oSpec.ORemainder.Overrides, voxRemainder));
        }
        else if (oSpec.ORemainder is not null)
        {
            aDegradations.Add(new Degradation("empty_region",
                "field.remainder declared but the vdb regions cover the whole part."));
        }
        return aRegions;
    }
}
