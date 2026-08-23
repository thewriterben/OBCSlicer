# Changelog

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
