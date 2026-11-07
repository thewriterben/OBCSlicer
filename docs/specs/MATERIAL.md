# `obc-material/0.1` — a pinned material record from the Open Filament Database

> 2026-11-04. Closes the ROADMAP item "Open Filament Database as cited material source".
> Companion to OpenDesignCore ADR-0013 (`FilamentRef`: opaque pinned strings, never
> dereferenced at run time). This record is the *dereferenced* form, produced once,
> hashed, and consumed by `obc3mf` by hash.

## Why
Temperature grading, purge estimates and "will this override melt the part" checks
all need material facts, and the slicer must not invent them. The Open Filament
Database (OFD, <https://openfilamentdatabase.org>) publishes a versioned JSON API with a
`manifest.json` that lists the sha256 and size of every artifact for a dataset
version. That makes a pin falsifiable: the record states dataset version, path,
sha256 and retrieval time, and anyone can re-fetch and compare.

## Command
```
obcslice material pin --brand bambu_lab --material PLA --filament basic \
    --out examples/materials/bambu-lab-pla-basic.material.json
```
Offline (tests, air-gapped): add `--from-file FILAMENT.json --manifest-file MANIFEST.json`.
Exit 2 and nothing written when the bytes do not match the manifest, the path is not
in the manifest, a required field is missing, or the record's own `slug`/`material`
disagree with the request.

## Record (canonical JSON, see CanonicalJson.cs)
| key | content |
|---|---|
| `record` | `obc-material/0.1` |
| `source` | `database`, `dataset_version`, `manifest_generated_at`, `manifest_url`, `path`, `url`, `sha256`, `size`, `schema`, `retrieved_utc` |
| `filament` | `id` (OFD id), `name`, `brand`, `material`, `slug`, `discontinued` |
| `properties` | lifted numeric fields, **as the source's own JSON text**, key suffixed with the unit from the OFD filament schema: `density_g_cm3`, `diameter_tolerance_mm`, `min/max_print_temperature_c`, `min/max_bed_temperature_c`, `min/max_chamber_temperature_c`, `max_volumetric_speed_mm3_s`, `min/max_fan_speed_pct`, `dry_temperature_c`, `dry_time_h`, `min_nozzle_diameter_mm`, `shore_hardness_a/d`, … |
| `absent_in_source` | lifted keys this filament does not publish (listed, never defaulted) |
| `not_in_source` | what OFD never carries: `shrinkage`, `tensile_strength`, `modulus`, `glass_transition`, `composition`. A record is not a citation for these. |
| `slicer_profiles` | per slicer: `id`, `generic_id`, `profile_name` (OrcaSlicer's `OGFA00` etc.) |
| `links` | `data_sheet_url`, `safety_sheet_url` when published |
| `tool` | `obcslice` 0.1.0 |

Numbers are strings on purpose: `1.24` stays `"1.24"`. Nothing is converted to a
float and back, so the record is byte-stable across languages (BINGO/ODC contract).

## Consumption: obc3mf
A part may name a record:
```json
{ "mesh": "body.stl", "filament": 1, "material": "pla.material.json",
  "overrides": { "nozzle_temperature": "220" } }
```
- The record's sha256, filament id/name and dataset version enter the part's entry
  in the `obc-project/0.2` compile record.
- `nozzle_temperature` and `nozzle_temperature_initial_layer` overrides must lie
  inside `[min_print_temperature_c, max_print_temperature_c]`, else exit 2 with the
  range quoted. If the filament publishes no range, the compile proceeds with a
  recorded degradation `material_temperature_unverified`.

## Not done
- Purge/flush estimates: OFD has no inter-material purge data; that stays a TODO(source)
  until a cited source exists (OrcaSlicer's flush matrix is per-printer calibration, not a fact about the filament).
- Shrinkage compensation: OFD cannot be the source (above). ODC `compensate` keeps its own cited table.
- The OFD schema URL moved between our first and second fetch on the same day
  (`schemas/filament.json` → `schemas/filament_schema.json`, discovered via `schemas/index.json`).
  The record cites the path that exists in the pinned dataset; expect to revisit.
