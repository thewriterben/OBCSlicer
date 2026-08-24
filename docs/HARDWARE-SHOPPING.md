# Hardware shopping note — scanner + foaming filament (researched 2026-08-23)

What the two remaining physical frontiers need, and what to buy. Prices are August 2026 street prices; re-check before ordering.

## 3D scanner (unlocks scan-compare boundary validation)

Requirement comes from ODC's `compare`: it judges print deviations (~0.05–0.3mm shrinkage, band-boundary placement) against a **declared** scanner accuracy, so the scanner should be ~3× better than the deviations it judges. 0.02mm-class or better; 0.1mm-class is the same magnitude as the signal and useless for significance testing.

| Option | Price | Accuracy | Verdict |
|---|---|---|---|
| **3DMakerpro Seal Lite** | ~$349 | 0.02mm acc / 0.07mm res, blue light, turntable | **Best value** — adequate for the 20mm-cube part class |
| Revopoint MINI 2 | ~$829 | 0.02mm class, maturer software | Step up if budget allows |
| 3DMakerpro Seal (pair) | <$1000 | 0.01mm class | Only sub-$1k 0.01mm option |
| Creality Ferret SE | ~$199 | 0.1mm | Skip for compare; ok for cradle-fit scans only |

Notes: matte PLA scans fine; dark/glossy parts need scanning spray. STL export feeds ODC's declared-units import boundary directly (`run-cradle` / `compare`, AUTO refused).

## Foaming filament (unlocks temperature→density grading, the 2025 paper's case (c))

| Option | Price | Specs |
|---|---|---|
| **eSUN ePLA-LW** | ~$30/kg | foams ~220% vol, density to 0.54 g/cm³ | **Value pick — buy TWO spools** |
| ColorFabb LW-PLA | pricier | up to 65% lighter, best-documented print guides | Reference brand |

Mechanism: prints like normal PLA below ~215–230°C; at 230–260°C expands 2–3× (cut flow to 40–60%).

**Why two spools:** temperature is filament-scope and cannot ride per-volume overrides (FIELD-SPEC.md caveat). The working recipe on the K2 + CFS: same LW filament in two lanes, two filament profiles differing only in temperature — per-band *filament* assignment becomes per-band *density*. Recipe in FIELD-SPEC.md.

Sources: 3dprinting.com small-format scanner roundup 2026 · 3dtechvalley.com small-object scanners 2026 · cadfinity.com hobbyist guide · esun3d.com ePLA-LW · colorfabb.com LW-PLA + print guide · spoolhound.com LW-PLA guide · Springer s40691-025-00446-7 (foaming characterization).
