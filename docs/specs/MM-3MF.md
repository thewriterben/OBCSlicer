# Multi-material 3MF project — what obc3mf emits (v0.1)

The compiled-project route (RESEARCH.md §2): discrete multi-material without touching libslic3r. obc3mf takes N meshes with filament-slot assignments and emits a 3MF the fork already knows how to read. Facts below cite fork source at `542cd18d19`.

## Archive layout

```
[Content_Types].xml               OPC content types (model + rels)
_rels/.rels                       root relationship → 3D/3dmodel.model
3D/3dmodel.model                  3MF core spec: all objects, indexed meshes, build items
Metadata/model_settings.config    BBS/Orca per-object settings (the multi-material part)
```

`3D/3dmodel.model` is the path the importer expects (`bbs_3mf.cpp` L192, `MODEL_FILE`). Core-spec XML, `unit="millimeter"`, one `<object id="N">` per part with welded indexed `<vertices>/<triangles>`, `<build>` items with 4×3 row-major transforms.

## The multi-material mechanism

`Metadata/model_settings.config` (`BBS_MODEL_CONFIG_FILE`, L211) is read by `_handle_start_config_object/_metadata` and matched to model objects **by id**. Any `<metadata key value>` on an object lands in `ModelObject::config` (L2201). The importer defaults a missing `extruder` to 1 and normalizes single-volume objects (L2271–2291). So:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<config>
  <object id="1">
    <metadata key="name" value="body"/>
    <metadata key="extruder" value="1"/>
  </object>
  <object id="2">
    <metadata key="name" value="grip"/>
    <metadata key="extruder" value="2"/>
    <metadata key="nozzle_temperature" value="230"/>   <!-- any per-object override -->
  </object>
</config>
```

`extruder` = filament slot (1-based) = CFS slot on the K2. Per-object process overrides use the same metadata mechanism — this is also the P3 seam: process-parameter grading compiles to per-region overrides here, no fork changes needed.

## Input manifest (obc3mf's own input)

```jsonc
{
  "output": "part.3mf",
  "parts": [
    { "mesh": "body.stl", "name": "body", "filament": 1 },
    { "mesh": "grip.stl", "name": "grip", "filament": 2,
      "overrides": { "nozzle_temperature": "230" } }
  ]
}
```

Meshes are millimetres — 3MF core has no other unit here and none is inferred. Binary and ASCII STL accepted; vertices welded exactly (bitwise-equal coordinates) to index the mesh.

## Validation at the boundary

Each mesh must be non-empty and closed: after welding, every edge shared by exactly two triangles. An open/non-manifold mesh is **refused** unless `--allow-open-mesh`, which records an `open_mesh` degradation in the compile record. Filament slots are ≥1; slot count vs. printer capacity is the slicer's problem, not silently remapped here.

## Compile record

`<output>.project.json`, canonical JSON (`obc-project/0.1`): input mesh hashes, part→filament map, overrides, output 3MF hash, tool version, degradations (always present). Feeds the slice record's model entry so the chain stays unbroken: design → **project compile** → slice → print.

## Verified against / still open

- Structure and metadata semantics read from the fork's importer/exporter (cited above); a **live import into the Orca GUI is still pending** — same blocker as the live slice run.
- Painted/per-face multi-material (`paint_color` triangle attributes) and per-volume (`<part>`) assignment: deliberately out of v0.1; objects-as-regions is the field-engine contract anyway.
