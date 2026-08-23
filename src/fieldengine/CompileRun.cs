using System.Globalization;
using System.Numerics;
using System.Text;
using ObcSlice;
using PicoGK;

namespace FieldEngine;

/// <summary>Field compile: mesh + axis-band material field → per-material region
/// meshes (watertight by construction — voxel-derived) + obc3mf manifest +
/// deterministic field record. Deliberately timestamp-free like ODC sidecars.</summary>
public static class CompileRun
{
    public const string StrToolVersion = "0.1.0";

    public sealed record Outcome(
        string StrRecordPath,
        IReadOnlyList<string> RegionPaths,
        IReadOnlyList<Degradation> Degradations);

    public static Outcome Execute(FieldSpec oSpec, string strOutDir)
    {
        Directory.CreateDirectory(strOutDir);
        var aDegradations = new List<Degradation>();
        string strMeshSha = SliceRecord.StrHashFile(oSpec.StrMeshPath);
        string strPicoGk = typeof(PicoGK.Library).Assembly.GetName().Version?.ToString() ?? "unknown";

        int nAxis = oSpec.StrAxis switch { "x" => 0, "y" => 1, _ => 2 };
        var aRegionFiles = new List<(string StrPath, string StrSha, int NFilament, int NTriangles)>();

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

            BBox3 oBox = voxPart.oCalculateBoundingBox();
            float fLoBound = (nAxis switch { 0 => oBox.vecMin.X, 1 => oBox.vecMin.Y, _ => oBox.vecMin.Z })
                - 2 * oSpec.FVoxelSizeMm;
            float fHiBound = (nAxis switch { 0 => oBox.vecMax.X, 1 => oBox.vecMax.Y, _ => oBox.vecMax.Z })
                + 2 * oSpec.FVoxelSizeMm;

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
                BBox3 oSlabBox = new(vecSlabMin, vecSlabMax);
                Voxels voxSlab = new(Utils.mshCreateCube(oLib, oSlabBox));
                Voxels voxRegion = voxPart & voxSlab;
                fLo = fHi;

                if (voxRegion.bIsEmpty())
                {
                    aDegradations.Add(new Degradation("empty_region",
                        $"Band {i + 1} (filament {oBand.NFilament}) intersects no part volume; " +
                        "no region emitted."));
                    continue;
                }

                Mesh mshRegion = voxRegion.mshAsMesh();
                if (mshRegion.nTriangleCount() == 0)
                    throw new InvalidDataException($"Band {i + 1}: meshing produced zero triangles.");

                string strRegionPath = Path.Combine(strOutDir,
                    $"region_{i + 1}_f{oBand.NFilament}.stl");
                // Two-arg call like ODC's runs: the trailing (Vector3?, float) params
                // are offset/scale with sane defaults — passing 0 scale zeroes the mesh.
                mshRegion.SaveToStlFile(strRegionPath, Mesh.EStlUnit.MM);
                aRegionFiles.Add((strRegionPath, SliceRecord.StrHashFile(strRegionPath),
                    oBand.NFilament, mshRegion.nTriangleCount()));
            }
        }

        if (aRegionFiles.Count == 0)
            throw new InvalidDataException("No band produced any region — nothing to print.");
        if (aRegionFiles.Select(r => r.NFilament).Distinct().Count() < 2)
            aDegradations.Add(new Degradation("single_material",
                "All surviving regions use one filament; the result is effectively single-material."));

        // obc3mf manifest (v0.2 shape: printer block passed through verbatim).
        // assembly:true — stacked bands overlap in XY and must be volumes of one object.
        var oSbManifest = new StringBuilder();
        oSbManifest.Append("{ \"output\": \"field.3mf\", \"assembly\": true, \"printer\": ")
            .Append(oSpec.OPrinter.GetRawText()).Append(", \"parts\": [");
        for (int i = 0; i < aRegionFiles.Count; i++)
        {
            if (i > 0) oSbManifest.Append(", ");
            oSbManifest.Append("{ \"mesh\": \"").Append(Path.GetFileName(aRegionFiles[i].StrPath))
                .Append("\", \"name\": \"").Append(Path.GetFileNameWithoutExtension(aRegionFiles[i].StrPath))
                .Append("\", \"filament\": ").Append(aRegionFiles[i].NFilament.ToString(CultureInfo.InvariantCulture))
                .Append(" }");
        }
        oSbManifest.Append(" ] }");
        string strManifestPath = Path.Combine(strOutDir, "manifest.json");
        File.WriteAllText(strManifestPath, oSbManifest.ToString());

        // Field record — canonical, deterministic, timestamp-free (like ODC sidecars).
        var oBandsRecord = new List<object?>();
        for (int i = 0; i < oSpec.Bands.Count; i++)
            oBandsRecord.Add(new Dictionary<string, object?>
            {
                ["filament"] = oSpec.Bands[i].NFilament,
                ["up_to_mm"] = oSpec.Bands[i].FUpToMm is float f
                    ? f.ToString("F3", CultureInfo.InvariantCulture) : null,
            });
        var oRecord = new Dictionary<string, object?>
        {
            ["record"] = "obc-field/0.1",
            ["units"] = "mm",
            ["voxel_size_mm"] = oSpec.FVoxelSizeMm.ToString("F3", CultureInfo.InvariantCulture),
            ["inputs"] = new Dictionary<string, object?>
            {
                ["mesh"] = Path.GetFileName(oSpec.StrMeshPath),
                ["mesh_sha256"] = strMeshSha,
                ["field"] = new Dictionary<string, object?>
                {
                    ["type"] = "axis_bands",
                    ["axis"] = oSpec.StrAxis,
                    ["bands"] = oBandsRecord,
                },
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
}
