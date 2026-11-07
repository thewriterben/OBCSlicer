# Changelog

## 2026-11-04 — obc-material/0.1: the Open Filament Database as a cited material source

- `obcslice material pin --brand --material --filament --out`: fetches one OFD filament record and `manifest.json`, verifies the bytes against the manifest sha256/size for the stated `dataset_version`, and writes a canonical `obc-material/0.1` record (docs/specs/MATERIAL.md). Numbers are carried as the source's own JSON text with unit-suffixed keys; fields the filament lacks are listed in `absent_in_source`; what OFD never carries (shrinkage, mechanical data) is named in `not_in_source` so the record cannot be mis-cited (ADR-0013 in OpenDesignCore). `--from-file/--manifest-file` for offline use. Refusals exit 2 with nothing written.
- obc3mf: a part may name a `"material"` record. Its hash, filament id and dataset version enter the compile record; `nozzle_temperature` / `nozzle_temperature_initial_layer` overrides outside the published print range are refused, a missing range is a recorded degradation.
- First pin on disk: `examples/materials/bambu-lab-pla-basic.material.json` (OFD dataset 2026.11.04). 32/32 tests (5 new, all offline fixtures).

## 2026-08-23 — obcslicer-ui: the GUI front-end

- `src/obcslicer-ui`: loopback web GUI (127.0.0.1:8771) in the studio-core dashboard style — HttpListener + one self-contained HTML page, zero packages. Three panels mirror the pipeline: field spec editor → compile; project compile + slice (profiles, slicer path); stage + propose (approval stays in the studio dashboard). Live printer state and pending approvals polled from studio-core; every record and degradation rendered verbatim in the activity log — the UI shows refusals, it never softens them.
- obcslice/obc3mf run in-process; fieldengine runs as a subprocess (PicoGK stack), auto-probed or via `OBCSLICE_FIELDENGINE`. Last-used paths persist to `.tools/ui-config.json`.
- Launch: `run-ui.cmd` (builds, opens browser, serves). 27/27 tests (handler-level: dry-run slice, refusal passthrough, tool-error mapping). Verified live: page serves, studio/printer state flowing.

## 2026-08-23 — vdb_regions: the OpenVDB/OpenVCAD interop seam, live

- Field type `vdb_regions` (spec → `obc-field/0.2`): per-material OpenVDB volumes intersected with the part, priority-ordered on overlap, **explicit** `remainder` filament required for unclaimed volume — refusal over silent assignment. Input VDBs content-hashed into the field record.
- `fieldengine make-vdb sphere|box`: author material volumes without OpenVCAD (mm, explicit voxel size).
- **Live: sphere.vdb (r=8, cube center) → spherical filament-2 core in a filament-1 cube → 82 toolchanges across 100 layers.** Any OpenVDB producer — OpenVCAD included — can now drive multi-material G-code through the pipeline as pure file interop (ADR-0003 honored: no OpenVCAD code anywhere).
- PicoGK gotcha documented: ambient-mode overloads (`voxSphere`, `mshCreateCube`, `voxFromVdbFile`, `new Voxels()` without a Library) throw under instance mode — use the Library-taking overloads.
- Also: build placement centered on plate (earlier commit) after the first physical print landed in the bed corner.

## 2026-08-23 — FIRST PHYSICAL PRINT: the field-banded cube exists

- The K2 Plus printed `47a9eb838791-3867ba336621.gcode` to completion (1907mm filament): a 20mm cube, Generic PLA below z=10mm, PLA High Speed above, one toolchange — compiled from a declared axis-band field through the full chain, every hop hashed, both actions human-approved.
- The studio jobs ledger holds the complete audit trail: upload (approved, executed) → print (approved, executed) → cancel after the CFS lane-mapping halt (approved) → print (approved, executed, complete). The failed first start produced a documented field lesson (ORCA-CLI.md: empty-lane "runout"), not a mystery.
- Project state: everything the original ask named is now demonstrated live — custom slicing layer (P1), OpenVCAD-style fields → multi-material (P2 + Field Engine), STL texturizer bridge, OBC-ecosystem integration (propose-only studio seam, provenance chain), plus P3 process grading. Remaining frontiers: `vdb_grid` input, BINGO evidence linkage, boundary-accuracy measurement (scan-compare), temperature-responsive filament.

## 2026-08-23 — P3: per-band process overrides, functionally graded infill live

- Field spec bands accept `overrides` (any string-valued slicer key; `extruder`/`filament` refused — material comes from `filament`). They flow: field spec → manifest parts → obc3mf per-`<part>` metadata → Orca applies per-volume.
- **Live: single-filament cube with `sparse_infill_density` 80% below z=10mm and 10% above → 2.94× extrusion ratio across the boundary.** Functionally graded density on stock hardware, no toolchange, no fork changes — the first FGM-class result of the project.
- Scope caveat recorded: per-volume overrides cover region-scope settings only; temperature grading goes via distinct filament slots with different-temp profiles.
- Manifest emission moved to System.Text.Json (proper escaping). 24/24 tests (assembly per-part override covered).

## 2026-08-23 — AdvancedStudio propose passthrough, live

- Contract read from the real source (`studio-core`: guard.py's propose-only Guard, staging.py's bounded resolve + `carries_design_hash`, app.py's `/api/propose` handlers) — no inference.
- `obcslicer-mcp` v0.2: `propose_upload` and `propose_print` POST to `/api/propose` (optional `X-Studio-Token`; base URL via `OBCSLICE_STUDIO`, default loopback :8770). Both only register a pending confirmation — the human approval and any print start remain in AdvancedStudio, by both sides' design.
- `stage_for_studio` now names staged G-code `<design12>-<gcode12>.gcode`, design anchor = ODC artifact hash when the slice record has one, else the sliced model's hash — so the studio's provenance check passes and the approval prompt names the design honestly.
- **Live-verified against the running studio**: field-cube G-code staged into the real staging dir, `gcode_upload` proposed, confirmation `65b4635b451e` registered with prompt "Upload '47a9eb838791-3867ba336621.gcode' (0.6 MB) … sliced from design sha256:47a9eb838791" — no rename warning. 24/24 tests.

## 2026-08-23 — Field Engine v0: declared fields to multi-material G-code, live

- `src/fieldengine` (net9.0 + PicoGK 2.2.0, the ODC stack; own `FieldEngine.slnx` since the sandbox that verifies the net8.0 projects lacks the PicoGK runtime): `obc-field/0.1` specs — mesh (mm, declared) + explicit voxel size + axis-band material field → per-band voxel regions (box-voxel booleans; `voxIntersectImplicit` SEH-faults under instance-Library mode, avoided and documented) → obc3mf manifest + deterministic timestamp-free field record.
- obc3mf gains **assembly mode** (`"assembly": true`, recorded as `orca-project-assembly`): all parts become volumes of ONE object with per-`<part>` extruder metadata — required for regions that overlap in XY; as separate objects the slicer refuses (−100). API surface for PicoGK verified by reflection against the cached package; two live-run bugs found and fixed (SaveToStlFile trailing scale param zeroes meshes; stale `--no-build` binary).
- **Live acceptance: z-banded cube field spec → fieldengine → obc3mf → obcslice → `; filament: 2,1`, 100 layers, exactly one toolchange at z=10mm.** The full OpenVCAD-shaped chain — declared field to provenance-attested multi-material G-code — runs end to end on stock tooling.
- 24/24 sandbox tests (assembly structure covered); `docs/specs/FIELD-SPEC.md` written.

## 2026-08-23 — Multi-material toolchanges: solved (obc3mf v0.2)

- Root cause confirmed via GUI check: Orca honors per-object extruders only for its own project files. Reverse-engineered the minimal sufficient structure from a GUI-saved reference (thanks Benji) by ablation + delta-debugging: thumbnails/slice-info droppable, `project_settings.config` mandatory, 653 → **22 keys**, identity fields must stay empty (preset-name matching crashes), machine facts must come from the caller.
- obc3mf v0.2: emits the validated project structure (production-extension wrappers, per-object model files, plate/assemble blocks, 22-key scaffold); manifest gains a **required `printer` block** — machine facts are never defaulted; compile record now `obc-project/0.2` with structure + printer recorded. Deterministic UUIDs keep output byte-stable.
- **Live acceptance: real obc3mf output → obcslice → 50 × `T1` toolchanges in K2 Plus G-code.** The discrete multi-material path works on stock OrcaSlicer — no fork changes needed; the planned fork branch is superseded.
- 23/23 tests; findings (incl. CLI harness flakiness and the identical-filament collapse trap) recorded in MM-3MF.md.

## 2026-08-23 — BumpMesh headless bridge

- `texturize-cli.mjs` added to the stlTexturizer repo (`e1df624`, GPLv3 there, never here): the web app's exact export pipeline driven from Node — STL in, displaced binary STL out, all projection modes, PNG textures, deps pinned to the web importmap's three@0.170.0.
- Verified in-sandbox: 12-triangle cube + dots texture → 31,174 triangles, 0 open/0 non-manifold edges, and the output passes obc3mf's closed-mesh gate cleanly. Texture → compile → slice pipeline is now three working processes exchanging hashed files.

## 2026-08-23 — Live run against official OrcaSlicer V2.4.2

- Fetched the official V2.4.2 x64 portable into `.tools/` (gitignored) and ran the pipeline for real on the dev machine.
- **Proven end to end**: ODC scan-cradle artifact + its provenance sidecar (hash verified) → `obcslice` → 242 KB `plate_1.gcode` (27 layers, ~5 min) + slice record, exit 0. First complete design→slice provenance chain.
- obc3mf project: geometry accepted, both objects sliced. **But per-object extruder does not reach G-code in this CLI** — verified via two independent mechanisms (model_settings metadata; basematerials color groups), both present in importer source, both dropped downstream. Stopped at two strikes; findings + next steps in MM-3MF.md and ORCA-CLI.md (GUI check, then a gated fork branch honoring `extruder` for plain 3MFs).
- CLI hazards recorded: `--debug`/`--export-3mf` crash (0xE06D7363), fake BBL tag without plate data crashes (0xC0000005), `--logfile` writes nothing, GUI-subsystem console quirks. `inherits` presets resolve when resources sit next to the exe.

## 2026-08-23 — P2 increment 1: obc3mf multi-material project compiler

- `docs/specs/MM-3MF.md` — archive layout and the `model_settings.config` extruder mechanism, read from the fork's importer/exporter (`bbs_3mf.cpp` L192/L211/L2201/L2271–2291); per-object overrides via the same metadata seam become P3's process-grading target.
- `src/obc3mf` — zero-dep compiler: manifest → 3MF core-spec geometry (binary/ASCII STL in, exact-weld indexed meshes, mm declared never inferred) + per-object filament/overrides + canonical compile record (`obc-project/0.1`) attesting input and output hashes. Boundary gate: open/non-manifold meshes refused (`--allow-open-mesh` records an `open_mesh` degradation); `extruder` override rejected in favor of `filament`.
- 5 new tests (archive structure verified by parsing the emitted XML; refusal paths covered). 22/22 total, build clean.
- Field Engine proper (OpenVDB fields in) stays blocked on NuGet access for PicoGK; obc3mf is the emission half it will feed.

## 2026-08-23 — P1 increment 2: MCP surface

- `src/obcslicer-mcp` — stdlib-only stdio MCP server (newline-delimited JSON-RPC: initialize / tools/list / tools/call / ping). The official MCP SDK needs NuGet, which the verification sandbox can't reach; the csproj documents swapping to it later with `Tools` unchanged.
- Tools: `slice` (wraps the same `SliceOperation` as the CLI — one code path, two surfaces) and `stage_for_studio` (copies G-code + record hash-named into AdvancedStudio's staging dir; refuses records with `slice_failed`/`dry_run`/`output_missing` degradations and any G-code whose hash the record doesn't attest — tamper test included). Staging is not proposing: upload/print approval stays human, in AdvancedStudio (ADR-0004).
- Propose passthroughs deferred: AdvancedStudio's `/api/propose` payload schema must be read from its source (repo not currently connected), not inferred.
- Refactor: `obcslice` slice logic extracted to `SliceOperation`; `Program` is now a thin CLI over it.
- Verified: solution builds clean, 17/17 tests pass, live stdio smoke (initialize → tools/list) answers correctly.

## 2026-08-23 — P1 increment: obcslice wrapper + specs

- `docs/specs/ORCA-CLI.md` — headless CLI contract surveyed from fork source at `542cd18d19` (BambuStudio-lineage: `--load-settings`/`--load-filaments`/`--slice N`/`--outputdir`; G-code lands as `plate_N.gcode`; `--export-gcode` is commented out upstream). Caveats: inherits chains, no version flag, no determinism promise.
- `docs/specs/SLICE-RECORD.md` — `obc-slice/0.1`, `application/vnd.obc.slice+json`. Canonical JSON byte-compatible with ODC/BINGO (verified against Python `json.dumps` reference output). Degradations always present, failures are records too, design linkage verified *before* slicing.
- `src/obcslice` — zero-dependency net8.0 console tool: hashes model/profiles/binary, verifies ODC sidecar `artifact.sha256` against the model file (refuses on mismatch, exit 2), builds the Orca invocation, runs it, hashes outputs, writes the canonical slice record. net8.0 (not ecosystem net9.0) deliberately: verifiable in the sandbox available today; SDK 10 on the dev machine builds it unchanged.
- `tests/obcslice.Tests` — zero-dependency assert runner (no NuGet in the verification environment; `NuGet.config` clears sources to keep restore hermetic). 12 tests: canonical-JSON goldens incl. float rejection, record determinism + envelope shape, sidecar verify/refuse, argv golden, inherits detection, dry-run e2e, mismatch e2e.
- Verified: `dotnet build OBCSlicer.sln -c Release` clean, 0 warnings; `dotnet run --project tests/obcslice.Tests -c Release` → 12 passed, 0 failed (SDK 8.0.130, linux-x64 sandbox).

Still open in P1: run against a built `orca-slicer.exe` on the dev machine (flattened K2 Plus presets, ORCA-CLI.md open items), MCP `slice` tool + propose passthroughs, full ODC→print chain.

## 2026-08-23 — P0: plan

- Research report, hybrid architecture, ADRs 0001–0004, roadmap. Reviewed and approved same day.
