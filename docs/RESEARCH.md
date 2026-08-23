# Research report — custom slicer for the OBC ecosystem

Date: 2026-08-23. Sources: local repo survey (six folders) + web research. Everything here feeds ARCHITECTURE.md and the ADRs.

## 1. What exists locally

| Repo | State | Relevance |
|---|---|---|
| `OpenDesignCore` | Live. C#/.NET on PicoGK 2.2.0 + ShapeKernel; voxel SDF over OpenVDB; deterministic runs; SHA-256 provenance sidecars; MCP server; `handoff` stages STL+sidecar to a slicing workspace | The design-side producer. Already speaks OpenVDB — the natural home for a material-field engine |
| `OrcaSlicer_AS` | Fresh fork of OrcaSlicer (upstream remote wired, origin = thewriterben/OrcaSlicer_AS). C++17, libslic3r, Arachne, Catch2 tests | The toolpath kernel. AGPLv3 |
| `OBCSlicer` | Empty | Staging ground (this repo) |
| `OpenVCAD-Public` | README + images only — **source is private**, non-commercial license, access by request form | Capability reference and format peer, not a dependency (§4) |
| `stlTexturizer` | CNC Kitchen's BumpMesh: browser JS, displacement texturing (24 textures, triplanar/cylindrical/spherical projection), 3MF/STL export, GPLv3 | Surface-texture stage of the pipeline |
| `awesome-3d-printingODC` | Curated link list | Background only |

Ecosystem contracts already written down in ODC's wiki:

- **AdvancedStudio** (K2 Plus executor): reads execute, writes propose-only; `gcode_upload` + `print_start` are two separate human-approved proposals, both carrying `design_artifact_sha256`; append-only jobs ledger. **Slicing is deliberately not automated** — Creality Print's CLI crashes (0xC0000005), and ADR-0009 wants a human in the loop. This is the exact gap a trustworthy slicer surface fills.
- **ProjectBINGO**: SHA-256 asset graph; `design_provenance` extension links a fabricated unit back to the ODC run (`wiki/concepts/bingo-odc-provenance-contract.md`). A slicing record slots between design and fabrication evidence.
- **Oh-Ben-Claw**: hardware registry = component ground truth; MCP as the composition seam; registry drift is a known issue — consume, don't fork.

## 2. Multi-material slicing landscape

**OpenVCAD** (Wade et al. 2024, *Additive Manufacturing* 79:103912): implicit compiler where geometry is an SDF and material is a volume-fraction vector at every point; outputs PNG stacks (inkjet), FEA meshes, voxel grids (OpenVDB/NanoVDB). Explicitly *not* a slicer for FFF until:

**Gradient-aware slicing** (Wade, Beck, MacCurdy 2025, arXiv:2505.08093, published in *Additive Manufacturing* 2025): extracts iso-contours of the material field directly from the implicit representation; two toolpath strategies — one preserves the wall/skin/infill paradigm and partitions it into discrete process-state regions with intelligent ordering and purge towers; validated on (a) two-material mixing extruder, (b) five-head toolchanger, (c) **single-material temperature-responsive foaming filament** — (c) matters because temperature-graded slicing works on stock single-nozzle hardware, K2 Plus included.

**3MF volumetric + implicit extensions** (3MF Consortium): standardized carriage of voxel/SDF/field data — density maps, material fields — inside 3MF. Not supported by consumer slicers yet; it is the right interchange target for us and sidesteps OpenVCAD's licensing entirely.

**Community prior art**: Prusa ColorMix (open-source layer-alternating virtual color mixing), OrcaSlicer-FullSpectrum (virtual mixed filaments via alternating thin layers), classic mixing-hotend G-code (M163/M164 virtual tools). All discrete-approximation tricks — none carries a continuous field end to end.

**A compiled-project route** (arXiv:2607.25326, 2026): compile design intent into slicer *project files* (per-region settings in 3MF) rather than modifying the slicer — a low-cost phase-1 technique we should use before touching libslic3r internals.

## 3. Hardware reality check

The K2 Plus + CFS is a **discrete filament changer**: per-region/painted multi-material and layer-alternating virtual mixing work today; continuous composition gradients do not — they need a mixing extruder. What *is* gradable on stock hardware: process parameters (temperature, flow, speed) — the foaming-filament result above. Hence ADR-0002: discrete first, FGM behind a hardware-capability flag.

## 4. Licensing analysis (drives the architecture)

| Component | License | Consequence |
|---|---|---|
| OrcaSlicer | AGPLv3 | Fork changes stay AGPLv3, in `OrcaSlicer_AS`. Nothing AGPL is copied into Apache-2.0 repos |
| OpenVCAD / pyvcad | **Non-commercial**, Regents of University of Colorado; source private | Cannot be vendored, linked, or forked into any open repo here (AGPL and Apache both conflict with a non-commercial field-of-use term). Interop via *data formats only* (OpenVDB grids, 3MF volumetric). Gradient-slicing algorithms reimplemented clean-room from the papers, which are citable public research |
| BumpMesh / stlTexturizer | GPLv3 | GPLv3↔AGPLv3 are one-way compatible per GPLv3 §13, but the clean pattern is process separation: run it as a preprocessing step (headless or browser), exchange meshes via 3MF. No code copying into Apache-2.0 code |
| PicoGK / ShapeKernel | Apache-2.0 / MIT | Free to build the material-field engine on, matching ODC |

## 5. Determinism caveat (known, accepted)

OrcaSlicer parallelizes with TBB and does not promise byte-identical G-code across runs. ODC-grade byte-determinism is therefore **not claimed for the fork's output** in P1; the provenance sidecar records input hashes, full config digest, slicer commit, and the *output* G-code hash — reproducibility is "same inputs recorded, output attested," not "output re-derivable byte-identically." The material-field engine (our code) is held to ODC's stricter standard. Revisit if the fork gains a deterministic mode.

## 6. Conclusions carried into the plan

1. Fork for toolpaths, engine for fields — never rewrite Arachne, never teach C++ Orca about volumetric fields before the interchange format exists (ADR-0001).
2. Discrete multi-material + per-region project compilation first; temperature/process grading is the first "gradient" feature because it runs on owned hardware (ADR-0002).
3. OpenVCAD is a format peer. OpenVDB in, 3MF volumetric in/out, clean-room algorithms (ADR-0003).
4. The slicer's ecosystem value is the **provenance chain**: design hash → slice record → proposed upload → proposed print → BINGO evidence. AdvancedStudio's human-approval contract is kept intact — the slicer proposes, never prints (ADR-0004).

Sources: [OpenVCAD paper](https://matterassembly.org/assets/pdf/preprints/2024_OpenVCAD.pdf) · [Gradient-aware slicing, arXiv:2505.08093](https://arxiv.org/abs/2505.08093) · [ScienceDirect version](https://www.sciencedirect.com/science/article/abs/pii/S2214860425003276) · [3MF volumetric spec](https://github.com/3MFConsortium/spec_volumetric/blob/master/3MF%20Volumetric%20Extension.md) · [3MF volumetric/implicit release](https://cdfam.com/3mf-consortium-volumetric-and-implicit-extension-release/) · [OpenVCAD-Public](https://github.com/MacCurdyLab/OpenVCAD-Public) · [OpenVCAD on PyPI](https://pypi.org/project/OpenVCAD/) · [Prusa ColorMix](https://blog.prusa3d.com/our-new-open-source-colormix-model-in-prusaslicer-and-easyprint_136079/) · [Compiling design intent into slicer projects, arXiv:2607.25326](https://arxiv.org/html/2607.25326) · [OrcaSlicer](https://github.com/OrcaSlicer/OrcaSlicer) · [BumpMesh](https://github.com/CNCKitchen/stlTexturizer)
