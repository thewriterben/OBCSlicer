# Slice record — `obc-slice/0.1`

Media type: `application/vnd.obc.slice+json`. The missing middle link in the provenance chain: design sidecar (ODC) → **slice record** → upload/print proposals (AdvancedStudio) → fabrication evidence (BINGO).

## Encoding

Canonical JSON, byte-compatible with BINGO's `canonical_json` and ODC's `CanonicalJson.cs`: keys sorted ordinally, no whitespace, non-ASCII escaped `\uXXXX`, ASCII output. **No floating-point values** — measured quantities are strings with the unit in the key. The file on disk *is* the canonical bytes; the record's identity is the SHA-256 of the file. A record cannot contain its own hash.

Written next to the G-code as `<model-stem>.slice.json`.

## Schema v0.1

```jsonc
{
  "record": "obc-slice/0.1",
  "created_utc": "2026-08-23T17:04:11Z",          // ISO-8601 UTC, seconds
  "inputs": {
    "model":  { "name": "bracket.stl", "sha256": "<hex>", "media_type": "model/stl" },
    "design_provenance": {                          // null when absent (degradation recorded)
      "system": "opendesigncore",
      "schema": "odc/provenance/0.1",               // from the sidecar itself
      "sidecar_sha256": "<hex of sidecar file>",
      "artifact_sha256": "<hex>",                   // sidecar's artifact.sha256; MUST equal inputs.model.sha256
      "run_id": "unknown"                           // ODC sidecars are deterministic and carry no run id;
    },                                              // pass --design-run-id to record the ledger row
    "profiles": [                                   // order as passed to the slicer
      { "role": "machine",  "name": "…", "sha256": "<hex>" },
      { "role": "process",  "name": "…", "sha256": "<hex>" },
      { "role": "filament", "name": "…", "sha256": "<hex>", "slot": 1 }
    ],
    "overrides": { "<cli-key>": "<string value>" }  // CLI-level --set overrides; {} when none
  },
  "slicer": {
    "name": "OrcaSlicer_AS",
    "binary_sha256": "<hex of the executable>",
    "commit": "<git sha or \"unknown\">",
    "invocation": ["orca-slicer", "…"]              // argv, exactly as executed
  },
  "result": {
    "exit_code": 0,
    "outputs": [ { "name": "plate_1.gcode", "sha256": "<hex>", "plate": 1 } ],
    "stderr_tail": ""                               // last ≤2000 chars, ASCII-escaped
  },
  "degradations": [                                 // ALWAYS present; [] means clean
    { "code": "unflattened_profile", "detail": "process.json contains 'inherits'" }
  ],
  "determinism": "attested"                         // this fork's output is recorded, not re-derivable
}
```

## Rules

1. **Validation before trust.** A record with `exit_code != 0` or missing outputs is still written (failures are evidence too) but MUST carry a `slice_failed` degradation; consumers refuse to propose uploads from it.
2. **Design linkage.** When a design sidecar is given, obcslice verifies `sidecar.artifact.sha256 == sha256(model file)` **before slicing** and refuses on mismatch — same verification rule as the BINGO↔ODC contract, enforced at the boundary.
3. **No silent degradation.** Anything that weakens the attestation (unflattened profiles, unknown slicer commit, dry runs, missing design sidecar, nonzero exit) appears in `degradations`, never only in a log.
4. **Hash comparability.** All hashes SHA-256 hex lowercase, matching ODC's artifact store and BINGO's asset graph.

## Degradation codes (v0.1)

`no_design_provenance` · `unflattened_profile` · `slicer_commit_unrecorded` · `slice_failed` · `dry_run` · `output_missing`

## Deferred to v0.2

Registration of the media type string alongside ODC's sidecar type · per-plate filament usage/cost extraction from G-code headers · reference from AdvancedStudio `gcode_upload` proposals (`slice_record_sha256` beside `design_artifact_sha256`) · BINGO `content.files[]` entry pattern.
