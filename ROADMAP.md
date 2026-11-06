# Roadmap

Same rule as ODC: this file exists so scope questions have an answer other than "sure, let's add it." Each phase ends with something printable or measurable; no phase starts before the previous one ran end to end.

## P0 — Plan (this commit)

- [x] Repo survey, landscape research, licensing analysis (`docs/RESEARCH.md`)
- [x] Hybrid architecture + ADRs 0001–0004
- [x] **Benji reviews; nothing below starts without a go-ahead** (approved 2026-08-23)

## P1 — Provenance-carrying headless slice (the thin thread)

Close the ecosystem's actual gap first: ODC `handoff` stages STL + sidecar to a workspace, then a human slices in a GUI and provenance dies there.

- [x] CLI contract documented from fork source (`docs/specs/ORCA-CLI.md`) and **verified live** against official V2.4.2 portable (auto-fetched to `.tools/`): ODC artifact + sidecar → G-code + slice record, exit 0. Live-run hazards recorded in the spec (2026-08-23)
- [x] `obcslice` wrapper: mesh + profiles + design sidecar in → G-code + **slice record** out; sidecar↔model hash verified before slicing, refuse on mismatch; 12 tests green (2026-08-23)
- [x] Slice record schema v0.1 (`docs/specs/SLICE-RECORD.md`), media type `application/vnd.obc.slice+json`, byte-compatible with ODC/BINGO `canonical_json` — checked against Python reference (2026-08-23)
- [x] MCP surface (`obcslicer-mcp`, stdlib-only stdio JSON-RPC): `slice` + `stage_for_studio` (design+gcode-hash naming so AdvancedStudio's `carries_design_hash` check passes) + **`propose_upload`/`propose_print`** — contract read from studio-core source (guard.py/staging.py/app.py), propose-only, human approves in the dashboard. **Live-verified against the running studio: field-cube G-code staged and proposed, confirmation registered, honest approval prompt** (2026-08-23)
- [x] **End-to-end, physically: field spec → fieldengine → obc3mf → obcslice → stage → propose → human approved → K2 printed the two-material banded cube (state `complete`, 1907mm filament).** Jobs ledger holds the full audit trail (upload/print/cancel/print, all approved, design-hash-linked). One field lesson recorded: CFS lane mapping (ORCA-CLI.md) (2026-08-23)

## P2 — Discrete multi-material + texture pipeline

- [x] Field Engine v0 (`src/fieldengine`, net9.0 + PicoGK 2.2.0, own `FieldEngine.slnx` — needs the PicoGK stack, verified on the dev machine): axis-band material fields over a mesh → voxel regions → obc3mf assembly manifest → deterministic field record. **Live-verified end to end: field spec → G-code with the material change exactly at the band boundary** (`docs/specs/FIELD-SPEC.md`, 2026-08-23). OpenVDB-grid input (`vdb_grid`) is the next field type — PicoGK's `voxFromVdbFile` is the seam
- [x] 3MF emission half of the compiler: `obc3mf` — manifest (meshes + filament slots + per-object overrides) → fork-native 3MF project + canonical compile record (`obc-project/0.1`); closed-mesh gate (open meshes refused unless flagged, recorded); `docs/specs/MM-3MF.md` cites the importer code it targets. Live GUI import check pending, same blocker as the live slice (2026-08-23)
- [x] Field→region extraction from OpenVDB: `vdb_regions` field type — per-material VDB volumes ∩ part, priority-ordered, explicit remainder; `make-vdb` authoring utility. **Live: sphere.vdb → spherical two-material core, 82 toolchanges/100 layers.** The OpenVCAD-output interop seam works (2026-08-23). Volume-fraction multi-grid VDBs (argmax/dither) are the remaining half
- [x] **Multi-material toolchanges achieved without fork changes** — GUI check showed the gap is importer-wide for generic 3MFs; solved instead by emitting the validated minimal Orca project structure (GUI reference + delta-debugging: 653→22 project_settings keys). obc3mf v0.2 emits it; live-verified 50×`T1` in headless G-code from real obc3mf output (2026-08-23). The fork branch idea is superseded — stock Orca works
- [ ] CFS mapping: region→AMS/CFS slot with purge estimates; degradation (e.g. more regions than slots) reported in the slice record, never silent
- [x] BumpMesh headless bridge: `texturize-cli.mjs` committed to the stlTexturizer repo (GPL side, separate process per ADR-0004) — runs the exact web export pipeline from Node; verified 12-tri cube → 31k-tri watertight mesh → passes obc3mf's closed-mesh gate, exit 0 (2026-08-23). Hash chaining into the compile record: wire when the pipeline caller lands
- [x] **First physical two-material print: the z-banded field cube, printed 2026-08-23** — material change at z=10mm as compiled. Still worth doing: a textured+multi-material combined artifact, and measuring boundary accuracy vs the field spec

## P3 — Process-parameter grading (first field-driven G-code on owned hardware)

- [x] Per-band process overrides → per-region parameter overrides in compiled projects — **live-verified: graded infill density (80%→10% at z=10mm, 2.94× extrusion ratio), single filament, stock hardware, no fork changes** (FIELD-SPEC.md, 2026-08-23). Caveat recorded: per-volume overrides cover region-scope settings; filament-scope (temperature) grades via distinct filament slots instead
- [ ] Temperature grading via per-band filament slots mapped to same-material different-temp profiles (the 2025 paper's foaming case) — needs a spool of temperature-responsive filament to be worth validating
- [ ] Validation target: temperature-responsive foaming filament density gradient on the K2 (single material, stock hardware — the paper's third case)

## P4 — Ecosystem depth

- [ ] 3MF volumetric extension read (and write where the spec is stable) in the Field Engine
- [ ] BINGO: slice record referenced from fabrication evidence (extend the ODC↔BINGO contract with the middle link)
- [x] Open Filament Database as cited material source: `obcslice material pin` → `obc-material/0.1`, manifest-verified; obc3mf checks nozzle temperature overrides against it (docs/specs/MATERIAL.md). Purge data is **not** in OFD and stays TODO(source) (2026-11-04)
- [ ] Oh-Ben-Claw registry consumption where slicing needs component ground truth (e.g. embedded-part prints)

## P5 — True FGM (hardware-gated)

Blocked on acquiring a mixing toolhead. Until then this is a paper exercise and stays here.

- [ ] Composition gradients: iso-contour material extraction from fields (clean-room per Wade 2025), M163/M164 virtual tools, purge tower planning
- [ ] Virtual layer-mixing fallback for discrete changers (ColorMix/FullSpectrum-style) as a capability-flagged approximation

## Not ever

- Linking or vendoring OpenVCAD/pyvcad (ADR-0003)
- Print-start automation or any approval capability (ADR-0004)
- Rewriting toolpath generation that belongs upstream in Orca, or geometry that belongs in PicoGK/ShapeKernel
- Silent fallbacks of any kind

## Open questions

- Does upstream Orca's CLI expose everything P1 needs, or is a small fork patch required from day one?
- 3MF volumetric: consume only, or also emit for third-party consumers? Decide at P4 with the spec's maturity in view.
- Deterministic-slicing mode for the fork (single-threaded slicing pass): worth proposing upstream?
