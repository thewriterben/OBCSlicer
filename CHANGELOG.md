# Changelog

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
