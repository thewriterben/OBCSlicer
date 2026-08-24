# Hardware shopping note — scanner + foaming filament (researched 2026-08-23)

What the two remaining physical frontiers need, and what to buy. Prices are August 2026 street prices; re-check before ordering.

## 3D scanner (unlocks scan-compare boundary validation)

Requirement comes from ODC's `compare`: it judges print deviations (~0.05–0.3mm shrinkage, band-boundary placement) against a **declared** scanner accuracy, so the scanner should be ~3× better than the deviations it judges. 0.02mm-class or better; 0.1mm-class is the same magnitude as the signal and useless for significance testing.

| Option | Price | Accuracy | Verdict |
|---|---|---|---|
| Photogrammetry (RealityScan 2.0 free / Meshroom / KIRI / Polycam) | $0 | ~0.5mm class | Pipeline testing + cradle-fit only; never compare |
| Creality Ferret SE | ~$199 | 0.1mm | Skip for compare |
| 3DMakerpro Fox | <$250 | 0.07mm | Marginal |
| Revopoint INSPIRE 2 | ~$300 | hobby class | Marginal |
| **3DMakerpro Seal Lite** | ~$349 | 0.02mm acc / 0.07mm res, blue light, turntable | **Best value** — adequate for the 20mm-cube part class |
| Creality Ferret Pro | ~$399 | 0.1mm, wireless/color | Skip for compare |
| Revopoint POP3 Plus | ~$500–700 | 0.05mm class | Adequate all-rounder, not great |
| Revopoint MINI 2 | ~$829 | 0.02mm, blue light, small-part specialist | Step up; maturest small-part software |
| Einstar / Einstar 2 | ~$859–959 | 0.05mm res (laser mode on 2) | Better for medium/large than tiny parts |
| **Creality CR-Scan Otter** | ~$899 | 0.02mm point acc, 4-lens small+medium | **Best single do-everything unit**; covers ODC's whole scan-to-fit range, same ecosystem |
| 3DMakerpro Seal (pair) | <$1000 | 0.01mm class | Only sub-$1k 0.01mm option |
| Revopoint MetroX | ~$1,200 | 0.03mm laser | Best accuracy/$ if reverse-engineering too; laser skips spray on dark/glossy |
| Revopoint MetroY Pro | ~$1,759 | metrology | Beyond hobby needs |
| Matter and Form THREE | ~$3,000 | 33 micron, standalone, no dots | Metrology tier |
| Creality RaptorX | ~$3,133–3,199 | 0.02mm, wireless blue laser + NIR | Metrology tier; overkill here |

Decision shortcut: **Seal Lite** if cube-class validation is the whole job · **Otter** for one scanner across all ODC part sizes · **MetroX** if dark/glossy-without-spray matters. Matte PLA scans fine on any 0.02mm-class unit.

Notes: matte PLA scans fine; dark/glossy parts need scanning spray. STL export feeds ODC's declared-units import boundary directly (`run-cradle` / `compare`, AUTO refused).

## Foaming filament (unlocks temperature→density grading, the 2025 paper's case (c))

| Option | Price | Specs |
|---|---|---|
| **eSUN ePLA-LW** | ~$30/kg | foams ~220% vol, density to 0.54 g/cm³ | **Value pick — buy TWO spools** |
| ColorFabb LW-PLA | pricier | up to 65% lighter, best-documented print guides | Reference brand |

Mechanism: prints like normal PLA below ~215–230°C; at 230–260°C expands 2–3× (cut flow to 40–60%).

**Why two spools:** temperature is filament-scope and cannot ride per-volume overrides (FIELD-SPEC.md caveat). The working recipe on the K2 + CFS: same LW filament in two lanes, two filament profiles differing only in temperature — per-band *filament* assignment becomes per-band *density*. Recipe in FIELD-SPEC.md.

Sources: 3dprinting.com small-format scanner roundup 2026 · 3dtechvalley.com small-object scanners 2026 · cadfinity.com hobbyist guide · esun3d.com ePLA-LW · colorfabb.com LW-PLA + print guide · spoolhound.com LW-PLA guide · Springer s40691-025-00446-7 (foaming characterization).
