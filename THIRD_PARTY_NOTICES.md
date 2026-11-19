# Third-party notices

Murmur's own source is covered by the existing [MIT License](LICENSE). Bundled dependency license texts and notices are retained in `app/Licenses/` and copied into `Murmur.app/Contents/Resources/Licenses/` by the build.

The entries below record upstream sources and their verification scope. Bundled license copies are retained in `app/Licenses/`; dates on individual entries describe those checks.

## Bundled in the app

`app/build.sh` copies the `ModelBench` helper into `Murmur.app`. That helper is built from `module-one/models/Runner`, which links FluidAudio. The build also copies FluidAudio's resource bundle when it exists. So FluidAudio ships in binary form inside the app.

### FluidAudio

- Source: https://github.com/FluidInference/FluidAudio
- Pinned revision: `0b1f46289fe27d95b5e66ad8be46e64f5ee02ae7` (from `module-one/models/artifact-pins.json` and `module-one/models/Runner/Package.swift`).
- License: Apache License, Version 2.0. Verified from the LICENSE file at the pinned revision: https://github.com/FluidInference/FluidAudio/blob/0b1f46289fe27d95b5e66ad8be46e64f5ee02ae7/LICENSE
- The LICENSE file has no filled-in copyright line, and there is no NOTICE file at the pinned revision (`NOTICE` returned 404).
- Release duty: include a copy of the Apache-2.0 text with the distributed app.

FluidAudio's package includes these targets, which are linked into the FluidAudio library:

#### fastcluster (via FluidAudio's `FastClusterWrapper` target)

- FluidAudio vendors `Sources/FastClusterWrapper/fastcluster_internal.hpp` from upstream fastcluster. Its header reads: "Until package version 1.1.23: © 2011 Daniel Müllner; All changes from version 1.1.24 on: © Google Inc."
- Upstream license: BSD 2-Clause. Verified at https://github.com/fastcluster/fastcluster/blob/master/LICENSE (upstream master, not a pinned revision).
- Release duty: reproduce the copyright notice, conditions and disclaimer in the documentation of a binary distribution.
- Whether the linker keeps this code in `ModelBench`, which uses only transcription, is unchecked. Including the notice is the safe choice.

#### NemoTextProcessing (via FluidAudio's binary target)

- Prebuilt xcframework from FluidInference/text-processing-rs v0.3.1, per FluidAudio's `Package.swift` at the pinned revision.
- License: Apache-2.0, as declared by the v0.3.1 release's `Cargo.toml`; the exact release `LICENSE`, `NOTICE`, and `THIRD-PARTY-LICENSES.md` are retained in `app/Licenses/Nemo-LICENSE.txt`, `app/Licenses/Nemo-NOTICE.txt`, and `app/Licenses/Nemo-THIRD-PARTY-LICENSES.md`.
- The local Swift 6.4 package dump selected FluidAudio's `Package@swift-6.2.swift`, whose default trait enables `NemoTextProcessing`; build workspace metadata resolves the v0.3.1 xcframework, and the built helper contains FluidAudio `TextNormalizer` symbols.
- These checks do not establish which individual framework components are included in the final executable. Verify that list and its required notices for the exact binary before distribution.

## Murmur for Windows

### Swift standard library (word difference)

- `windows/src/Murmur.Core/History.cs` (`WordDifference`) is a C# port of the linear-space Myers difference in `stdlib/public/core/Diffing.swift` from https://github.com/swiftlang/swift (`main`, fetched 2026-11-19). The port keeps the same edits as the Mac app when several shortest edit scripts exist.
- Header: "Copyright (c) 2015 - 2019 Apple Inc. and the Swift project authors. Licensed under Apache License v2.0 with Runtime Library Exception." License: https://swift.org/LICENSE.txt
- Release duty: ship the Apache-2.0 text with the Windows app and state that the file was modified (translated to C#).

### sherpa-onnx and ONNX Runtime (local speech helper)

- `windows/src/Murmur.Speech` uses sherpa-onnx 2.0.1 (k2-fsa, Apache-2.0, https://github.com/k2-fsa/sherpa-onnx) through the `org.k2fsa.sherpa.onnx` NuGet packages. The nuspec and the repository LICENSE at v2.0.1 declare Apache-2.0.
- The runtime packages bundle ONNX Runtime 1.28.2 (Microsoft, MIT, https://github.com/microsoft/onnxruntime). The packages carry no license file, so the MIT license is not yet checked from the package. Check it before release.
- The Parakeet Unified ONNX conversion stays under the NVIDIA Open Model License, recorded below.

### LLamaSharp and llama.cpp (local cleanup helper)

- `windows/src/Murmur.Cleanup` uses LLamaSharp 0.30.0 (MIT, https://github.com/SciSharp/LLamaSharp) and its `LLamaSharp.Backend.Cpu` 0.30.0 native libraries. These are built from llama.cpp and ggml (MIT, https://github.com/ggml-org/llama.cpp).
- Release duty: ship both MIT texts with the Windows app. Before release, check the copyright lines against each project's LICENSE at the pinned versions; they are not yet checked.

### SCOWL English word list (bundled in the Windows app)

- `windows/shared/lexicon/english-scowl-60.txt.gz`: 88,210 lowercase American English words from SCOWL (Spell Checker Oriented Word Lists) by Kevin Atkinson, https://github.com/en-wl/wordlist, release `rel-2026.02.25`, size 60. It stands in for the spell checker the Mac uses to tell ordinary words from names.
- License: the SCOWL notice in `windows/shared/lexicon/LICENSE` (permission to use, copy, modify, distribute and sell, provided the copyright notice and permission notice appear in all copies and supporting documentation; provided "as is"). The AU, UKACD and WordNet terms in the upstream `Copyright` file do not apply to this list, which is American, size 60 and drawn from none of those parts. Checked November 19, 2026.
- **Modified:** words with capital letters, abbreviations and special categories removed, then sorted and gzipped (`windows/tools/lexicon/`). Provenance, SHA-256 and measurements: `windows/shared/lexicon/README.md`.
- Release duty: ship `windows/shared/lexicon/LICENSE` with the Windows app.

### Vedu Scribe 0.8B, GGUF (downloaded at run time on Windows)

- Apache-2.0.
- **Modified:** converted from the MLX checkpoint `heyvedu/vedu-scribe-0.8b@7dced58`, a fine-tune of `Qwen/Qwen3.5-0.8B`. Steps: dequantize, repack, requantize to Q8_0, and edit the header to 24 layers (`module-one/cleanup/convert_gguf.py`).
- These are not the original weights, and HeyVedu and the Qwen team have not endorsed them.
- The hosted copy needs the Apache-2.0 text and this modification notice. It is staged locally until the user approves the upload.

## Downloaded at run time, not bundled

### Parakeet Unified English 0.6B

The model weights are not in the repository or the app bundle. They are downloaded to `~/Library/Caches/Dictation/` from the pinned Hugging Face revision, and file checksums are verified. Settings can download pinned models or use an existing verified model folder.

- Upstream model: `nvidia/parakeet-unified-en-0.6b`, https://huggingface.co/nvidia/parakeet-unified-en-0.6b
  - License: NVIDIA Open Model License Agreement (Hugging Face tag `nvidia-open-model-license`). The card says the model "is ready for commercial/non-commercial use".
  - License text: https://www.nvidia.com/en-us/agreements/enterprise-software/nvidia-open-model-license/ (last modified October 24, 2025).
  - Section 3 requires anyone who distributes the model or a derivative to include a "Notice" text file with: "Licensed by NVIDIA Corporation under the NVIDIA Open Model License".
- Converted model used by the app: `FluidInference/parakeet-unified-en-0.6b-coreml`, revision `d32e972dd4315f1dc3f6be28fb2aab0ab3e80358`, https://huggingface.co/FluidInference/parakeet-unified-en-0.6b-coreml
  - Its card is tagged `cc-by-4.0`, says "Released under the CC-BY-4.0 license", and names `base_model: nvidia/parakeet-unified-en-0.6b`. It makes no statement about the upstream NVIDIA license.
  - The conversion's CC-BY-4.0 label does not relicense NVIDIA's weights; treat the NVIDIA Open Model License as governing. The differing declarations remain unresolved; review both model cards before redistribution.

Because Murmur downloads the weights rather than redistributing them, the NVIDIA notice duty may not apply to the app itself. The app shows the model license before download, and this notice is reproduced here:

> Licensed by NVIDIA Corporation under the NVIDIA Open Model License

### Parakeet CTC 110M (dictionary support)

Optional download from Settings → Dictionary. It rescores on-device transcripts toward the user's dictionary. It is not bundled; Murmur downloads the pinned files to FluidAudio's model folder (`~/Library/Application Support/FluidAudio/Models/parakeet-ctc-110m-coreml`) and verifies their checksums.

- Upstream model: `nvidia/parakeet-tdt_ctc-110m`, https://huggingface.co/nvidia/parakeet-tdt_ctc-110m. Its Hugging Face card is tagged `cc-by-4.0`.
- Converted model used by the app: `FluidInference/parakeet-ctc-110m-coreml`, revision `accdafd8cf8a2ff1cabe3c11e54416b405d409aa`, https://huggingface.co/FluidInference/parakeet-ctc-110m-coreml. Its card is tagged `cc-by-4.0` and names `base_model: nvidia/parakeet-tdt_ctc-110m`. Both checked October 4, 2026 through the Hugging Face model API.
- License: Creative Commons Attribution 4.0 International, https://creativecommons.org/licenses/by/4.0/. It is recorded in `module-one/models/artifact-pins.json`, so the app shows it as pinned before download.
- Attribution: "Parakeet TDT-CTC 110M" by NVIDIA, converted to Core ML by FluidInference, used under CC BY 4.0. Murmur uses the files unmodified.

### Speaker diarization, pyannote community-1 (speaker separation)

Optional download from Settings → Meetings → "Separate speakers after a meeting". It is not bundled; Murmur downloads the pinned files to `~/Library/Caches/Dictation/module-one/speaker-diarization-df2625a` and verifies their checksums. The helper loads them directly; FluidAudio's own downloader is never used for them.

- Converted models used by the app: `FluidInference/speaker-diarization-coreml`, revision `df2625ac79a7ac6b65ad868fee6d80f320da4232` (the revision FluidAudio `0b1f462` pins in `ModelNames.swift`), https://huggingface.co/FluidInference/speaker-diarization-coreml. Files: `Segmentation.mlmodelc`, `FBank.mlmodelc`, `Embedding.mlmodelc`, `PldaRho.mlmodelc` and `plda-parameters.json`, 21 files, 21,599,417 bytes. The legacy online files (`pyannote_segmentation`, `wespeaker*`) and `PLDA.mlmodelc` are not downloaded.
- License: Creative Commons Attribution 4.0 International, https://creativecommons.org/licenses/by/4.0/. The repository's `NOTICE.md` at that revision scopes CC-BY-4.0 to exactly these Community-1-derived files, and its `LICENSE` is the CC-BY-4.0 text. Checked October 16, 2026.
- Upstream: the pyannote speaker-diarization-community-1 pipeline, published by pyannote under CC-BY-4.0, https://huggingface.co/pyannote/speaker-diarization-community-1. The PLDA parameters come from Brno University of Technology / BUT Speech@FIT, licensed CC-BY-4.0 by the rights holder.
- Modification: Fluid Inference converted the PyTorch components to Core ML, fixed and enumerated input shapes, applied mixed-precision storage, separated the FBank frontend from the embedding backend, and compiled them for Apple platforms. Murmur uses the converted files unmodified.
- Attribution, shown in Settings and in the download sheet: "Speaker separation uses modified Core ML conversions by Fluid Inference of the pyannote speaker-diarization-community-1 pipeline (pyannote segmentation, WeSpeaker embeddings) with PLDA parameters from BUT Speech@FIT, licensed under CC-BY-4.0."
- Citations retained from the repository's README:
  - Plaquet, A. and Bredin, H. "Powerset multi-class cross entropy loss for neural speaker diarization." Proc. INTERSPEECH 2023.
  - Wang, H., Liang, C., Wang, S., Chen, Z., Zhang, B., Xiang, X., Deng, Y. and Qian, Y. "Wespeaker: A research and production oriented speaker embedding learning toolkit." ICASSP 2023.
  - Landini, F., Profant, J., Diez, M. and Burget, L. "Bayesian HMM clustering of x-vector sequences (VBx) in speaker diarization: theory, implementation and analysis on standard tasks." Computer Speech & Language, 2022.

## Cloud services

Any custom OpenAI-compatible endpoint the user configures is an optional service reached with the user's own API key. No provider code or SDK is bundled; Murmur sends plain HTTPS requests. Their terms apply to the user's account, not to redistribution. See [provider integration](app/PROVIDERS.md) for data flows, and review the selected provider’s current terms.

## Bundled fonts and retained dependency notices

DM Sans and Instrument Serif are bundled under the SIL Open Font License; their license texts are in `app/Fonts/` and ship beside the fonts.

`app/Licenses/` retains FluidAudio's Apache-2.0 text and all five notices from `ThirdPartyLicenses/` at the pinned revision, plus the text-processing-rs v0.3.1 license, NOTICE and dependency list. Including a notice does not imply every optional subsystem is used by Murmur.

Before distributing a binary, review the exact linked Rust dependency license texts and the upstream/conversion model-license mismatch described above. These notices do not resolve ownership questions or grant permissions beyond the upstream licenses.

## Managed local cleanup (optional model)

- **Vedu Scribe 0.8B** (`heyvedu/vedu-scribe-0.8b`, revision `7dced58…`, Apache-2.0): downloaded only when the user chooses Local model and asks for it; verified against pinned checksums.
- **mlx-swift 0.31.6** (prebuilt `Cmlx.framework`, SHA-256 `a202bf1d…ab86c9`), **mlx-swift-lm 2.31.3**, **swift-transformers**, **swift-jinja**, **swift-huggingface**, **yyjson**: bundled in `LocalCleanupRunner`; license texts are in `Contents/Resources/Licenses/LocalCleanup-LICENSES.txt`.
