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

## Open (verify when a built binary exists on this machine)

- Whether `--load-settings` accepts absolute Windows paths with spaces un-escaped (quote the whole semicolon list).
- Plate numbering when `--arrange 1` re-plates objects.
- Whether K2 Plus profiles flatten cleanly (no `inherits` leftovers) via GUI export.
