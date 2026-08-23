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

## Deferred

`vdb_grid` field type (OpenVDB volume-fraction grids in — PicoGK's `voxFromVdbFile` is the seam; this is where OpenVCAD output plugs in) · per-band process overrides (P3 temperature grading rides the same `<part>` metadata) · non-axis fields (radial, mesh-distance) · fractional/dithered boundaries.
