# OBCSlicer

Slicing layer for the OBC ecosystem: discrete multi-material today, functionally-graded (FGM) slicing later, with provenance from design hash to G-code to fabricated part.

**This repo is the staging ground and integration layer, not the slicer fork.** C++ changes to the slicer itself live on feature branches in `OrcaSlicer_AS` (AGPLv3). This repo holds the specs, contracts, the material-field engine, and glue.

## Read first, in this order

1. `ARCHITECTURE.md` — the hybrid two-component design and where each piece lives
2. `docs/RESEARCH.md` — landscape survey and the licensing analysis that shaped it
3. `DECISIONS.md` — ADRs 0001–0004
4. `ROADMAP.md` — phases P1–P5

## The one-paragraph version

OrcaSlicer (forked as `OrcaSlicer_AS`) stays the toolpath kernel — nobody should rewrite Arachne. A separate **material-field engine** (C#/.NET beside OpenDesignCore, on PicoGK/OpenVDB) owns volumetric multi-material: it compiles material fields into discrete regions the fork can slice now, and into gradient-aware toolpaths later. OpenVCAD is a *format peer*, never a dependency — its source is non-commercial-licensed and cannot touch an AGPL codebase; we interoperate through OpenVDB grids and the 3MF volumetric extension, and implement gradient slicing clean-room from the published papers. BumpMesh (stlTexturizer) runs as a separate preprocessing process. Every artifact crossing a boundary carries a SHA-256 provenance sidecar compatible with OpenDesignCore's ledger and ProjectBINGO's asset graph; prints are proposed to AdvancedStudio, never started by software.

## GUI

`run-ui.cmd` → http://127.0.0.1:8771 — field spec to proposed print in three panels, with records and degradations shown verbatim and live printer state from AdvancedStudio. Approval always stays in the studio dashboard.

## Verify with

```
dotnet build OBCSlicer.sln -c Release
dotnet run --project tests/obcslice.Tests -c Release     # zero-dep assert runner; nonzero exit on failure
```

Toolchain: any .NET SDK ≥ 8 (projects target net8.0; see the csproj note). Zero package dependencies; `NuGet.config` clears feeds so restore is hermetic and offline.

## Status

P1 mostly done: `obcslice` (headless slice + slice record) and `obcslicer-mcp` (stdio MCP: `slice`, `stage_for_studio`). P2 started: `obc3mf` compiles meshes + filament assignments into fork-native multi-material 3MF projects. Blocked on externals: live runs need a built `orca-slicer.exe`; the propose passthrough needs the AdvancedStudio source connected; the Field Engine needs NuGet (PicoGK). See `ROADMAP.md` and `CHANGELOG.md`.

## License

Code original to this repository: Apache-2.0 (matches OpenDesignCore, ADR-0010 there).
Slicer-fork work is contributed to `OrcaSlicer_AS` under AGPLv3 and never copied here.
