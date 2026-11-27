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
- **Silero VAD** (`silero_vad.onnx`, snakers4/silero-vad, MIT, https://github.com/snakers4/silero-vad) is bundled beside `MurmurSpeech.exe` (`windows/src/Murmur.Speech/silero_vad.onnx`, 643,854 bytes, SHA-256 `9e2449e1…bb1fd6`, from the k2-fsa/sherpa-onnx `asr-models` release). Whisper dictation uses it to cut recordings at silence.
- **Whisper large-v3-turbo** by OpenAI, MIT (https://huggingface.co/openai/whisper-large-v3-turbo), exported to int8 ONNX for sherpa-onnx by csukuangfj (`csukuangfj/sherpa-onnx-whisper-turbo`, revision `2ca6ff69…`). That repository has no license tag; the sherpa-onnx export code is Apache-2.0. The Windows app downloads the pinned files on request and verifies their SHA-256. It does not bundle the model.
- **Parakeet TDT 0.6B v3** by NVIDIA, CC BY 4.0, as int8 ONNX for sherpa-onnx by csukuangfj (`csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8`, revision `ed5d7456…`). The Windows app downloads the pinned files on request; the download confirmation shows the upstream and conversion cards. [Upstream model](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3)

### LLamaSharp and llama.cpp (local cleanup helper)

- `windows/src/Murmur.Cleanup` uses LLamaSharp 0.30.0 (MIT, https://github.com/SciSharp/LLamaSharp) and its `LLamaSharp.Backend.Cpu` 0.30.0 native libraries. These are built from llama.cpp and ggml (MIT, https://github.com/ggml-org/llama.cpp).
- Release duty: ship both MIT texts with the Windows app. Before release, check the copyright lines against each project's LICENSE at the pinned versions; they are not yet checked.

### Country flags (bundled in the Windows app)

- The dictation language picker shows flag images from **lipis/flag-icons** v7.6.0 (MIT, Copyright (c) 2013 Panayiotis Lipiridis, https://github.com/lipis/flag-icons), commit `0d8909b00aa8418802a5abf2afa82356b129a7ef`. Licence checked at that commit: MIT, the repository's only licence file (https://github.com/lipis/flag-icons/blob/0d8909b00aa8418802a5abf2afa82356b129a7ef/LICENSE). 33 unmodified `flags/4x3` SVGs (158,740 bytes) are in `windows/src/Murmur.App/Assets/Flags/`, with the upstream LICENSE (SHA-256 `fc0231cc…0336`) and the per-file SHA-256 list in that folder's README. Windows has no emoji flag glyphs, so the Mac's emoji flags are not used there.
- Release duty: ship `Assets/Flags/LICENSE` with the Windows app (the project copies it beside the SVGs).

### SCOWL English word list (bundled in the Windows app)

- `windows/shared/lexicon/english-scowl-60.txt.gz`: 88,210 lowercase American English words from SCOWL (Spell Checker Oriented Word Lists) by Kevin Atkinson, https://github.com/en-wl/wordlist, release `rel-2026.02.25`, size 60. It stands in for the spell checker the Mac uses to tell ordinary words from names.
- License: the SCOWL notice in `windows/shared/lexicon/LICENSE` (permission to use, copy, modify, distribute and sell, provided the copyright notice and permission notice appear in all copies and supporting documentation; provided "as is"). The AU, UKACD and WordNet terms in the upstream `Copyright` file do not apply to this list, which is American, size 60 and drawn from none of those parts. Checked November 19, 2026.
- **Modified:** words with capital letters, abbreviations and special categories removed, then sorted and gzipped (`windows/tools/lexicon/`). Provenance, SHA-256 and measurements: `windows/shared/lexicon/README.md`.
- Release duty: ship `windows/shared/lexicon/LICENSE` with the Windows app.

### Vedu Scribe 0.8B, GGUF (downloaded at run time on Windows)

- Apache-2.0.
- **Modified:** converted from the MLX checkpoint `heyvedu/vedu-scribe-0.8b@7dced58`, a fine-tune of `Qwen/Qwen3.5-0.8B`. Steps: dequantize, repack, requantize to Q8_0, and edit the header to 24 layers (`module-one/cleanup/convert_gguf.py`).
- These are not the original weights, and HeyVedu and the Qwen team have not endorsed them.
- Hosted at https://huggingface.co/clouddevaakash/vedu-scribe-0.8b-gguf (revision `edf275972c822fb7787226a89afa4282feb74ec4`), with the Apache-2.0 text and this modification notice. The Windows app downloads it from there, pinned by revision and SHA-256.

### Gemma 3 4B, GGUF (Windows local meeting notes, downloaded at run time)

- Gemma 3 4B instruction-tuned by Google, as the Q4_K_M GGUF `gemma-3-4b-it-Q4_K_M.gguf` from https://huggingface.co/ggml-org/gemma-3-4b-it-GGUF (revision `d0976223747697cb51e056d85c532013931fe52e`; 2,489,757,856 bytes; SHA-256 `882e8d2db44dc554fb0ea5077cb7e4bc49e7342a1f0da57901c0802ea21a0863`). The card is tagged `gemma` and names `base_model: google/gemma-3-4b-it`. Checked November 21, 2026 through the Hugging Face model API, and the SHA-256 and size recomputed on the file the benchmark ran.
- Terms: Gemma is provided under and subject to the Gemma Terms of Use, https://ai.google.dev/gemma/terms. This is not an open-source licence. The use restrictions are the Gemma Prohibited Use Policy, https://ai.google.dev/gemma/prohibited_use_policy.
- Murmur does not change the weights and does not bundle or redistribute them: the Windows app downloads the file from Hugging Face only when the user asks for it, and the download confirmation shows the terms notice and the Prohibited Use Policy link before any byte is fetched. Murmur does not provide the model as a service. If a release ever bundles or hosts the file, the terms' redistribution duties apply (pass on the use restrictions and this notice, state the terms' location, mark any change) and this section must be updated first.
- Runs through LLamaSharp and llama.cpp (see above), locally; the transcript is never sent anywhere.
- Windows only: the Mac does not use it.

### Speaker separation (Windows, downloaded at run time)

- pyannote segmentation 3.0 as ONNX by csukuangfj (`csukuangfj/sherpa-onnx-pyannote-segmentation-3-0`), MIT.
- WeSpeaker VoxCeleb ResNet34 speaker embedding (`csukuangfj/speaker-embedding-models`, file `wespeaker_en_voxceleb_resnet34.onnx`), Apache-2.0.
- Pinned by revision and SHA-256 in `windows/shared/windows-models.json`.

### Velopack (installer and updates)

- Velopack 1.2.161 (MIT) installs the Windows app and updates it from GitHub releases tagged `windows-v*`.

### Windows App SDK and WinUI 3 (app shell)

- Microsoft.WindowsAppSDK 2.7.0 (Microsoft, https://github.com/microsoft/WindowsAppSDK). Release duty: check the package's licence terms at the pinned version and ship the required notice; not yet checked.

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

- **Murmur Cleanup 2B** (`clouddevaakash/murmur-cleanup-2b`, revision `b591570…`, Apache-2.0; a fine-tune of `Qwen/Qwen3.5-2B` by the Qwen team, also Apache-2.0): downloaded only when the user chooses Local model and asks for it; verified against pinned checksums.
- **mlx-swift 0.31.6** (prebuilt `Cmlx.framework`, SHA-256 `a202bf1d…ab86c9`), **mlx-swift-lm 2.31.3**, **swift-transformers**, **swift-jinja**, **swift-huggingface**, **yyjson**: bundled in `LocalCleanupRunner`; license texts are in `Contents/Resources/Licenses/LocalCleanup-LICENSES.txt`.

## Multilingual dictation

- **WhisperKit** by Argmax, MIT. Murmur links the WhisperKit target at revision `9f99b5cb6a907730543823ad271b82532782a899`. Its license is bundled in `app/Licenses/WhisperKit-LICENSE.txt`.
- **Whisper large-v3-turbo** by OpenAI, MIT, converted and quantized to Core ML by Diction Labs. The app downloads the pinned `dictionlabs_whisper-large-v3-turbo-q6q8` artifacts and tokenizer. [Conversion and license](https://huggingface.co/DictionLabs/whisperkit-coreml/blob/87410e60d77684d6a83ccafca325e8ebf3300470/README.md)
- **Parakeet TDT 0.6B v3** by NVIDIA, CC BY 4.0, converted to Core ML by FluidInference. The app downloads the pinned conversion. The conversion card tags the weights CC BY 4.0 and its prose names Apache 2.0. The download confirmation discloses both statements. [Upstream model](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3) · [Pinned conversion](https://huggingface.co/FluidInference/parakeet-tdt-0.6b-v3-coreml/blob/7dd20fe6b1797d35f5e3307e8b1732d9a178edfe/README.md)
