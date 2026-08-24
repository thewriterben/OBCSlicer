# Field spec — `obc-field/0.1`

The Field Engine's input and record formats. The engine compiles a material field over a mesh into discrete per-material regions — the OpenVCAD-shaped step, implemented clean-room on PicoGK/OpenVDB (ADR-0003). **Live-verified 2026-08-23**: z-banded cube → two voxel regions → obc3mf assembly project → headless slice → `; filament: 2,1` with exactly one toolchange at the band boundary.

## Input spec (JSON)

```jsonc
{
  "geometry": { "mesh": "part.stl", "units": "mm" },   // units declared; only "mm" in v0.1
  "voxel_size_mm": "0.30",                              // explicit, never defaulted
  "field": {
    "type": "axis_bands",                               // v0.1's only field type
    "axis": "z",                                        // x | y | z, in the mesh's own frame
    "bands": [                                          // ascending; last band open-ended
      { "filament": 1, "up_to_mm": "10.0" },
      { "filament": 2 }
    ]
  },
  "printer": { "nozzle_diameter_mm": "0.4", "printable_width_mm": 350,
               "printable_depth_mm": 350, "printable_height_mm": 350 }  // → manifest, never defaulted
}
```

## Behavior and invariants

- Geometry is **not recentred** — bands are in the mesh's coordinate frame; moving the mesh would silently change what the boundaries mean.
- Resolution floor: interior band thickness < 2 voxels → refused loudly.
- Regions are cut as `part ∩ slab` with PicoGK box-voxel booleans (the ODC-proven pattern; `voxIntersectImplicit`'s managed callback SEH-faults under instance-Library mode in PicoGK 2.2.0 — found live, avoided).
- Region meshes are voxel-derived → watertight by construction → pass obc3mf's gate.
- Empty bands degrade (`empty_region`), never silently vanish; an effectively single-material result degrades (`single_material`).

## Outputs

`region_<band>_f<filament>.stl` per non-empty band · `manifest.json` — obc3mf manifest with **`"assembly": true`** (stacked regions overlap in XY; as separate objects Orca refuses to slice them (−100), as volumes of one object with per-`<part>` extruder metadata they slice correctly) · `field.json` — canonical record, **deterministic and timestamp-free** like ODC sidecars: mesh hash, spec echo, region hashes, manifest hash, tool + PicoGK versions, degradations.

## Chain position

```
(ODC / OpenVCAD / any producer) mesh
   └► fieldengine  → regions + manifest + field record
        └► obc3mf  → assembly project 3MF + compile record
             └► obcslice → G-code + slice record → staging → human-approved print
```

## Per-band process overrides (P3, live-verified)

Bands accept `"overrides": { "<slicer key>": "<string value>" }`, flowing into the manifest parts and obc3mf's per-`<part>` metadata, which Orca applies per-volume. **Live result (2026-08-23): single-filament cube, `sparse_infill_density` 80% below z=10mm / 10% above → 2.94× extrusion ratio across the boundary — functionally graded density on stock hardware, no toolchange.** `extruder`/`filament` are refused as overrides — material comes from `filament`. Caveat: only region-scope (per-volume) slicer settings apply; filament-scope settings (temperatures) do not ride per-part metadata — grade those via distinct filament slots with different profiles instead.

## `vdb_regions` — OpenVDB interop (v0.2, live-verified)

```jsonc
"field": { "type": "vdb_regions",
  "regions": [ { "vdb": "core.vdb", "filament": 2, "overrides": {} } ],
  "remainder": { "filament": 1 } }
```

Each region is an OpenVDB volume file — producible by OpenVCAD, ODC/PicoGK, or the bundled authoring utility (`fieldengine make-vdb sphere|box`, mm, explicit voxel). Semantics: `region_i = (part ∩ vdb_i) − earlier regions` (priority order where volumes overlap); part volume claimed by no region goes to the **explicit** `remainder` filament or the compile refuses — no silent material assignment. Input VDBs are content-hashed into the field record.

**Live result (2026-08-23): sphere.vdb (r=8 at cube center) → spherical filament-2 core inside a filament-1 cube → 82 toolchanges across 100 layers** — true 3D volumetric material assignment, the OpenVCAD-output seam working end to end.

PicoGK 2.2.0 gotcha (found live): short overloads (`voxSphere(center,r)`, `mshCreateCube(bbox)`, `voxFromVdbFile(path)`, `new Voxels()`) assume `Library.Go` ambient mode and throw under instance mode — always use the `Library`-taking overloads.

## Deferred

Volume-fraction/multi-grid VDBs (current: one indicator volume per region; fractional fields need argmax/dither compilation) · non-axis analytic fields (radial, mesh-distance) · dithered boundaries.
