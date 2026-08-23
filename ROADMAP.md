# Roadmap

Same rule as ODC: this file exists so scope questions have an answer other than "sure, let's add it." Each phase ends with something printable or measurable; no phase starts before the previous one ran end to end.

## P0 — Plan (this commit)

- [x] Repo survey, landscape research, licensing analysis (`docs/RESEARCH.md`)
- [x] Hybrid architecture + ADRs 0001–0004
- [x] **Benji reviews; nothing below starts without a go-ahead** (approved 2026-08-23)

## P1 — Provenance-carrying headless slice (the thin thread)

Close the ecosystem's actual gap first: ODC `handoff` stages STL + sidecar to a workspace, then a human slices in a GUI and provenance dies there.

- [x] CLI contract documented from fork source (`docs/specs/ORCA-CLI.md`); **build on this machine + live invocation check still pending** (2026-08-23)
- [x] `obcslice` wrapper: mesh + profiles + design sidecar in → G-code + **slice record** out; sidecar↔model hash verified before slicing, refuse on mismatch; 12 tests green (2026-08-23)
- [x] Slice record schema v0.1 (`docs/specs/SLICE-RECORD.md`), media type `application/vnd.obc.slice+json`, byte-compatible with ODC/BINGO `canonical_json` — checked against Python reference (2026-08-23)
- [x] MCP surface (`obcslicer-mcp`, stdlib-only stdio JSON-RPC): `slice` (executes — writes only its own outdir) + `stage_for_studio` (hash-named staging; refuses failed/dry-run/unattested records). Propose passthroughs deliberately deferred: AdvancedStudio's `/api/propose` schema must be read from its source, not inferred — connect that folder to wire it (2026-08-23)
- [ ] End-to-end: ODC enclosure run → obcslice → proposal → human approves → K2 prints → job ledger row references design *and* slice hashes

## P2 — Discrete multi-material + texture pipeline

- [ ] Field Engine skeleton: OpenVDB grid in (SDF + per-material volume fraction), mm + voxel size declared, validation gate, deterministic
- [ ] Field→region compiler v0: threshold/argmax volume fractions into discrete regions → 3MF project with per-region filament assignment (compiled-project route, RESEARCH.md §2) — no libslic3r changes needed
- [ ] CFS mapping: region→AMS/CFS slot with purge estimates; degradation (e.g. more regions than slots) reported in the slice record, never silent
- [ ] BumpMesh headless bridge: textured-mesh preprocessing step with hash chaining (input mesh sha → textured mesh sha in the record)
- [ ] Print a two-material + textured test artifact; measure region boundary accuracy vs field

## P3 — Process-parameter grading (first field-driven G-code on owned hardware)

- [ ] Scalar process fields (temperature, flow, speed) → per-region parameter overrides in compiled projects
- [ ] Fork branch: gated option consuming per-region temperature overrides cleanly (purge/ordering per the 2025 paper's region strategy)
- [ ] Validation target: temperature-responsive foaming filament density gradient on the K2 (single material, stock hardware — the paper's third case)

## P4 — Ecosystem depth

- [ ] 3MF volumetric extension read (and write where the spec is stable) in the Field Engine
- [ ] BINGO: slice record referenced from fabrication evidence (extend the ODC↔BINGO contract with the middle link)
- [ ] Open Filament Database as cited material source for purge/compatibility data
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
