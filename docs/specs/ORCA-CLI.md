# OrcaSlicer_AS headless CLI contract (surveyed 2026-08-23)

Facts read from the fork source at commit `542cd18d19` — `src/libslic3r/PrintConfig.cpp` (CLI option defs, ~L11700–12500) and `src/OrcaSlicer.cpp` (CLI main, gcode export ~L5640–6430). This is the BambuStudio-lineage CLI, not classic Slic3r's.

## Invocation shape

```
orca-slicer [model files: 3mf/stl/step/obj]
  --load-settings  "machine.json;process.json"     # semicolon-joined
  --load-filaments "fil1.json;fil2.json"            # one per used slot, order = slot order
  --slice N                                         # 0 = all plates, i = plate i
  --outputdir DIR
  [--curr-bed-type TYPE] [--debug N] [--arrange 1] [--orient 1]
  [--load-defaultfila 1] [--allow-newer-file] [--min-save]
  [--export-3mf out.3mf] [--export-slicedata DIR] [--uptodate]
```

## Verified behaviors

- `--slice` writes `plate_<n>.gcode` into `--outputdir` (OrcaSlicer.cpp L5718, L6316). With a `--pipe`/config-named variant, `plate_<n>_<name>.gcode`.
- Actions available: `slice`, `export_3mf`, `export_stl(s)`, `export_slicedata`, `uptodate`, `arrange`, `orient`, `repair`, `convert_unit`, plus transform ops (`scale`, `rotate*`, `cut*`, `merge`). `export_gcode`/`gcodeviewer` exist but are commented out — G-code comes from `--slice`, not an `--export-gcode` action.
- Non-3MF model input is accepted; settings must then come entirely from `--load-settings`/`--load-filaments`.
- Exit code 0 on success; multiple nonzero internal codes on failure (`flush_and_exit` paths). Treat any nonzero as failure; do not interpret specific codes yet.

## Caveats obcslice must handle

1. **Inherits chains.** `resources/profiles/**.json` presets use `inherits` and are not self-contained; CLI resolution depends on a populated `--datadir`. Recommended flow: export **flattened** presets from the GUI once, store them content-addressed, and feed those. obcslice records an `unflattened_profile` degradation when it sees an `inherits` key.
2. **No version flag surveyed.** Slicer identity is recorded as SHA-256 of the slicer binary itself (strong), plus optional `--slicer-commit` (human-friendly).
3. **Determinism not promised** by the fork (TBB parallelism) — slice records are *attestations* (RESEARCH.md §5).
4. Windows binary: `orca-slicer.exe` in the install dir; point obcslice at it with `--slicer` or `OBCSLICE_ORCA` env.

## Live-run findings (official V2.4.2 x64 portable, 2026-08-23)

Verified working:
- Full chain: ODC artifact STL + verified design sidecar → `obcslice` → `plate_1.gcode` (27 layers) + slice record, exit 0.
- Absolute Windows paths with spaces work when passed as separate argv entries (`ProcessStartInfo.ArgumentList`); no manual quoting.
- `inherits` presets **are resolved by the CLI** when run from the portable install (resources sit next to the exe) — caveat 1 softens to: unflattened profiles slice fine but make the attestation depend on the resources tree, so the degradation stays.
- obc3mf-emitted 3MF: geometry accepted, both objects sliced (100 layers, correct volume).

Broken / hazards in this build:
- **Per-object extruder assignment is not honored for non-project 3MF**: neither `model_settings.config` metadata nor core-spec `basematerials` color groups produced a `T1` (source-verified both importer paths exist; the value is lost in the CLI slicing path). Definitive GUI check pending; candidate fork work: a gated option honoring `extruder` metadata from plain 3MF.
- Claiming the BBL `Application` tag without full plate structure → hard crash `0xC0000005`.
- `--debug N` and `--export-3mf` → C++ exception `0xE06D7363`; `--logfile` accepted but writes nothing.
- GUI-subsystem exe: console redirection from PowerShell is unreliable; always run with redirected pipes (obcslice does).

Still open: plate numbering under `--arrange 1`; GUI import of obc3mf projects; whether the fork build (vs. official binary) behaves differently on any of the above.
