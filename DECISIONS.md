# Decisions

ADR format matches OpenDesignCore. Reversing any of these is expensive; argue now, not after they're load-bearing.

---

## ADR-0001 — Hybrid base: OrcaSlicer fork for toolpaths, separate field engine for volumetric material (2026-08-23)

**Context.** Options were: fork OrcaSlicer; write a slicer from scratch; or hybrid. A from-scratch kernel is 12+ months from matching Arachne wall generation, seam logic, and printer profile coverage. But teaching C++ Orca about volumetric material fields directly would be a deep, unmergeable divergence from upstream — and the review rules of that codebase demand gated, regression-free changes.

**Decision.** Hybrid. `OrcaSlicer_AS` (existing fork, upstream remote wired) gets small, option-gated, upstream-mergeable changes. A new Field Engine in this repo (C#/.NET on PicoGK/OpenVDB — the stack ODC already pins) owns material fields and compiles them *down* to what the fork already understands: 3MF projects with per-region assignments. The seam between them is a file format, not an API.

**Consequences.** Two codebases to verify; the fork tracks upstream (rebases are routine work); the Field Engine can be held to ODC-grade determinism while the fork cannot (RESEARCH.md §5). Cleanly reversible toward "fork-only" if the engine proves unnecessary; not reversible toward from-scratch without writing off the fork investment.

---

## ADR-0002 — Discrete multi-material first; FGM behind capability flags (2026-08-23)

**Context.** Owned hardware (K2 Plus + CFS) is a discrete filament changer. Continuous composition gradients need a mixing extruder. The 2025 gradient-aware slicing paper's single-material result — grading *process parameters* (temperature → foaming density) — runs on stock hardware.

**Decision.** Phase order: (1) discrete per-region multi-material via compiled 3MF projects; (2) process-parameter grading (temperature/flow) as the first field-driven feature, because it is exercisable on owned hardware today; (3) composition gradients (M163/M164 mixing, virtual layer-mixing) only behind a hardware-capability flag, when a mixing toolhead exists to validate against.

**Consequences.** No feature ships that cannot be printed and measured here. The Field Engine's field→region compiler is built once and reused for all three phases. Risk: capability flags rot if unexercised — each flagged path needs at least a golden-file test.

---

## ADR-0003 — OpenVCAD is a format peer, never a dependency (2026-08-23)

**Context.** OpenVCAD's source is private under a University of Colorado **non-commercial** license; pyvcad on PyPI carries the same terms. Both AGPLv3 (fork) and Apache-2.0 (this repo) are incompatible with a non-commercial field-of-use restriction. Its *papers*, however, are public research, and its voxel export formats (OpenVDB/NanoVDB) are open.

**Decision.** No OpenVCAD or pyvcad code, binary, or import anywhere in shipped repos. Interop is file-based: the Field Engine reads OpenVDB grids and (when the spec work lands) 3MF volumetric — producible by OpenVCAD, ODC/PicoGK, or anything else. Gradient-slicing algorithms are implemented clean-room from Wade et al. 2024/2025, cited in code.

**Consequences.** Users who own OpenVCAD can feed us its output; we never depend on it existing. We reimplement iso-contour material extraction ourselves — real work, but on our license terms, on a kernel (OpenVDB) we already run. Revisit only if OpenVCAD relicenses.

---

## ADR-0004 — Repo layout, licensing split, and the propose-only boundary (2026-08-23)

**Context.** Work spans an AGPLv3 fork, GPLv3 BumpMesh, Apache-2.0 ODC ecosystem, and a printer executor whose safety contract is "reads execute, writes are propose-only" with slicing deliberately left to humans (AdvancedStudio ADR-0002/0009).

**Decision.**
- This repo: specs, contracts, Field Engine, bridges. Apache-2.0. Nothing AGPL/GPL copied in.
- Fork work: branches in `OrcaSlicer_AS`, AGPLv3, tracked from here by branch name only.
- BumpMesh: separate process, 3MF in/out; improvements go upstream, not into a vendored copy.
- The slicer surface (CLI/MCP, P1) **slices and records; it proposes uploads/prints through AdvancedStudio's existing `/api/propose` and never gains an approval or start capability.** Automating slicing is in scope; automating the decision to print is not, and stays with the human in the dashboard.

**Consequences.** Three-repo workflow overhead, bought deliberately: license hygiene stays auditable, and the ecosystem's safety contract survives adding a slicer. Slice records give AdvancedStudio's ledger and BINGO's asset graph the missing middle link (design → **slice** → fabricate).
