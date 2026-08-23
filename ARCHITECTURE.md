# Architecture

Hybrid, two components, one contract between them.

```
  OpenDesignCore ──► material fields + meshes (OpenVDB / STL / 3MF + provenance sidecar)
        │
        ▼
  ┌─────────────────────────────┐        ┌──────────────────────────────┐
  │  Field Engine (this repo)   │  3MF   │  OrcaSlicer_AS (AGPL fork)   │
  │  C#/.NET · PicoGK · OpenVDB │ ─────► │  C++ · libslic3r · Arachne   │
  │  fields → regions/params    │ project│  toolpaths → G-code          │
  └─────────────────────────────┘        └──────────────┬───────────────┘
        ▲                                               │ G-code + slice sidecar
  OpenVCAD / BumpMesh                                   ▼
  (format peers, separate processes)      AdvancedStudio (propose-only) ──► BINGO evidence
```

## Components

### Field Engine (`src/`, this repo, Apache-2.0)

- **Responsibility:** compile a volumetric material/process field into something a toolpath slicer can execute: per-region 3MF project files with material and parameter assignments (P3), later gradient-aware toolpath hints (P5).
- **Depends on:** PicoGK/OpenVDB (same pinned stack as ODC), 3MF volumetric extension for interchange.
- **Accepts:** OpenVDB grids (SDF + material volume fractions or process-parameter scalar fields), meshes with mm units declared, ODC provenance sidecars.
- **Returns:** 3MF (core + production + volumetric where consumed), region→tool maps, process-parameter schedules; always with a provenance sidecar.
- **Does not:** generate toolpaths, talk to printers, or read OpenVCAD source/binaries. It consumes *files* OpenVCAD (or ODC, or anything) can emit.

### Slicer fork (`OrcaSlicer_AS` repo, AGPLv3 — work tracked here, code there)

Feature branches, each gated so upstream behavior is untouched when the option is off (their review rule):

- headless slice CLI hardened for our pipeline + slice-record sidecar emission
- discrete multi-material UX for CFS-class changers; consume Field Engine 3MF projects
- process-parameter grading (temperature/flow per region) — first FGM-adjacent feature that runs on a K2 Plus
- later: mixing-extruder virtual tools (M163/M164) and gradient toolpath support, capability-flagged

### Bridges (separate processes, never linked)

- **BumpMesh** (GPLv3): displacement texturing as a preprocessing step; mesh in → textured mesh out via 3MF. Candidate P2 work: a headless wrapper so it can sit in the pipeline; contributions go upstream to CNC Kitchen's repo.
- **OpenVCAD** (non-commercial): if the user has it installed, its OpenVDB/voxel exports are just files the Field Engine reads. No pyvcad dependency in any shipped code (ADR-0003).

## The provenance contract (the actual point)

Every hop appends, none rewrites:

```
design sidecar (ODC, exists)        sha256: geometry + inputs + versions + commit
  └► slice record (new, P1)         sha256: gcode; refs: design sha, mesh sha, profile digest,
                                    full config digest, slicer fork commit, capability flags,
                                    degradations (fallbacks NEVER silent — in the record)
       └► upload/print proposals    (AdvancedStudio, exists) design_artifact_sha256 threads through
            └► fabrication evidence (BINGO, contract drafted in ODC wiki)
```

Slice records use ODC's canonical-JSON rules (byte-compatible with BINGO `canonical_json`) so hashes are stable and comparable everywhere. Media type: `application/vnd.obc.slice+json` (register alongside ODC's sidecar type).

## Inherited invariants

Millimetres everywhere, units declared at boundaries, never inferred. Voxel size explicit per field, in the record. Geometry validated before slicing (manifold/watertight), fail loudly. No silent degradation. No invented material data — filament properties come from cited `data/` entries or the Open Filament Database, else `TODO(source)`. Field Engine held to ODC byte-determinism; fork output attested-not-rederivable (RESEARCH.md §5).

## Non-goals

Printer control (AdvancedStudio's job) · marketplace/settlement (BINGO's job) · geometry algorithms that belong in PicoGK/ShapeKernel or upstream Orca · shipping anything that links OpenVCAD or bypasses AdvancedStudio's human approval.
