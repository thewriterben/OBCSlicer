# Multi-material 3MF project — what obc3mf emits

> **v0.2 (2026-08-23): SOLVED.** Plain core-spec 3MF (v0.1 below) imports as geometry-only —
> Orca honors per-object extruders only for its own project structure. The minimal sufficient
> structure was established empirically (GUI-saved reference from Benji + delta-debugging
> against OrcaSlicer V2.4.2) and obc3mf now emits it. **Live-verified: obc3mf project →
> headless slice → 50 toolchanges (`T1`) in the G-code.** See "Validated minimal project
> structure" below; v0.1 sections are kept for the core-spec geometry details that still apply.

## Validated minimal project structure (v0.2)

```
[Content_Types].xml               rels + model types only
_rels/.rels                       → 3D/3dmodel.model only (thumbnail rels droppable)
3D/3dmodel.model                  production ext (requiredextensions="p"), Application tag
                                  "BambuStudio-02.06.00.51" + OrcaSlicer version tag +
                                  BambuStudio:3mfVersion=1; objects are component WRAPPERS
                                  (even ids 2,4,…) with p:UUID + p:path → per-object files;
                                  build items with p:UUIDs, printable=1, auto_drop=1
3D/_rels/3dmodel.model.rels       → the object files
3D/Objects/<name>_<odd>.model     one per part: inline mesh, odd ids (1,3,…), version meta
Metadata/model_settings.config    objects keyed by WRAPPER id: name + extruder + overrides +
                                  <part id=odd> (name, identity matrix); <plate> block
                                  (plater_id, filament_map_mode "Auto For Flush",
                                  filament_maps sized to slot count, model_instance entries);
                                  <assemble> block
Metadata/project_settings.config  the 22-key minimal set (below)
```

Droppable (verified): all thumbnail PNGs and their rels/metadata refs, `slice_info.config`, `filament_sequence.json`. **Not droppable:** `project_settings.config` — its absence (or an empty `{}`) hard-crashes the CLI (0xC0000005).

**The 22-key `project_settings.config`** (delta-debugged 653 → 22): filament arrays sized to slot count with inert values (`filament_colour` display palette, `filament_colour_type`, `filament_deretraction_speed` "nil", `filament_diameter` "1.75", `filament_ironing_{inset,spacing,speed}` "nil", `filament_is_support` "0", `filament_settings_id` "", `filament_shrink`/`filament_shrinkage_compensation_z` "100%", `filament_tower_interface_pre_extrusion_length` "0"), plus `notes`, `nozzle_diameter`, `print_sequence` "by layer", `print_settings_id` "", `printable_area`/`printable_height` (**from the manifest's required `printer` block — machine facts are never invented**), `printer_agent` "", `printer_extruder_id` ["1"], `printer_settings_id` "", `printer_structure` "undefine". Identity fields are deliberately empty: populating `print/printer_settings_id` with preset names triggered a crashing preset-matching path. Real filament data is loaded from profiles at slice time (`--load-filaments`), which is why inert values are honest here — use **distinct** filament profiles per slot, since BBL-project loading maps identical ones to a single filament.

**Harness caveat:** the V2.4.2 GUI-subsystem exe is flaky under some process harnesses (PowerShell console redirect, occasionally python subprocess) while consistently fine under obcslice's redirected-pipe invocation — validate through obcslice, nothing else.

---

# v0.1 reference (core-spec geometry; superseded for emission)

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

- Structure and metadata semantics read from the fork's importer/exporter (cited above).
- **Live CLI result (official 2.4.2, 2026-08-23): geometry imports and slices correctly (both objects), but per-object `extruder` does not reach the G-code** — no `T1` emitted, via either this spec's metadata mechanism or a core-spec `basematerials` color-group variant. The importer paths exist in source; the assignment is lost in the CLI slicing path for non-project files. See ORCA-CLI.md live-run findings.
- **GUI check result (2026-08-23): both objects show filament 1.** The gap is importer-wide for generic 3MFs, not CLI-only — Orca honors `model_settings.config` extruder assignments only for files it identifies as its own projects, and claiming that identity (`Application` tag) without the full project structure hard-crashes (0xC0000005).
- Two candidate fixes, decision pending a reference file: (a) **emit the full Orca project structure** — obtain a ground-truth by assigning filaments in the GUI and saving the project, then diff against our output and emit exactly what's needed (spec-by-example, no fork changes, works with stock Orca); (b) **fork patch**: honor `extruder` metadata from plain 3MFs (gated option) — cleaner file on our side but requires everyone to run the fork. (a) is preferred if the required structure is small; (b) if it drags in plate JSONs, slice info, and per-object model files.
- Painted/per-face multi-material (`paint_color` triangle attributes) and per-volume (`<part>`) assignment: deliberately out of v0.1; objects-as-regions is the field-engine contract anyway.
