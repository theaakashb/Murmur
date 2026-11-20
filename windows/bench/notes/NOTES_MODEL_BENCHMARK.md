# Local meeting notes benchmark

Phase 1 evaluates user-downloaded GGUFs for offline notes on Windows. The target PC has a Ryzen 9 5900X and 32 GB RAM. The comparison assumes CPU inference.

## Notes contract

The [Mac writer](../../../app/MeetingNotes.swift) numbers visible transcript lines from 1, labels them with speaker and time, and encloses them and optional user notes in separate nonce-tagged blocks. Treat those blocks as data. A decision requires agreement. An action requires a commitment or accepted request. Its owner and due date must be supported. Leave categories empty where the source has no qualifying items. Lecture mode reuses the fields for concepts, examples and perspectives and drops the meeting-specific agreement and commitment requirements.

The prompt asks for a sentence-case title of up to eight words, a two- to four-sentence summary, and one line per point, decision or action. The plain-text protocol uses `TITLE | ...`, `SUMMARY | ...`, `POINT | L… | ...`, `DECISION | L… | ...` and `ACTION | L… | owner | due | ...`. Apple Intelligence uses guided generation with a 1,200-token reply cap and up to 10 decisions, 12 actions, and 8 points, then converts the output to the same draft format. The tolerant parser accepts bullets, bold and fenced wrappers, bare numbers and bounded citation ranges, and missing action metadata. The writer enforces the title word limit. Sentence case and summary length are prompt requirements.

Mac on-device extraction budgets 5,000 characters of rendered lines per request for Apple's 4,096-token context. Mac custom and the current Windows writer budget 24,000 characters. The writer splits a context-overflowing map chunk once and reduces multiple drafts in batches, with one split on context overflow where a merge can still make progress. It grounds drafts before merging and grounds the final result again. The merge prompt asks for deduplication and the union of existing citations. The guard requires known citations, overlapping content, supported names, dates and numbers, and consistent polarity. It excludes note-taker-directed text from evidence, checks action ownership and commitment, and checks decision agreement. The final notes store stable transcript IDs for citations. Title and summary have separate free-text grounding checks.

[Mac meeting unit checks](../../../app/check_meetings.swift) cover parsing, grounding, prompts and long-input behavior. [Windows prompt and writer](../../src/Murmur.Core/MeetingNotes.cs) reuse exported Swift prompt data. The [31-fixture golden](../../golden/data/meeting_notes_corpus.json), built by [the Swift generator](../../golden/golden_meeting_notes_corpus.swift), tests grounding with constructed replies. The [model scorer](../../../app/tools/measure_meeting_notes.swift) runs the real Swift writer and judges emitted items against the [22-meeting corpus](../../../app/meeting-notes-corpus.json) and [9-meeting heldout set](../../../app/meeting-notes-heldout.json). The published bars are at most 0.20 unsupported items per completed meeting and zero invented names or numbers. The scorer also counts failed meetings and missed labelled decisions and actions. Failed meetings are excluded from the printed recall denominators, so treat an apparently low unsupported rate alongside the failure count. Citation overlap is a weak semantic check. The fixtures do not independently score summaries, owner accuracy, due dates or the lecture format.

## Candidate files

Sizes are exact bytes from the Hugging Face model API with `blobs=true`. Revisions identify the benchmark artifacts. Downloaded files are checked against published Hub LFS SHA-256 digests before benchmarking.

| Candidate and exact Hub revision | File | Bytes | Licence evidence |
| --- | --- | ---: | --- |
| [Vedu Scribe 0.8B](https://huggingface.co/clouddevaakash/vedu-scribe-0.8b-gguf/tree/edf275972c822fb7787226a89afa4282feb74ec4) `edf275972c822fb7787226a89afa4282feb74ec4` | `vedu-scribe-0.8b-Q8_0.gguf` | 811,843,296 | Hub card `apache-2.0`. Existing cleanup model used as baseline. |
| [Qwen2.5 3B Instruct](https://huggingface.co/Qwen/Qwen2.5-3B-Instruct-GGUF/tree/7dabda4d13d513e3e842b20f0d435c732f172cbe) `7dabda4d13d513e3e842b20f0d435c732f172cbe` | `qwen2.5-3b-instruct-q4_k_m.gguf` | 2,104,932,768 | [Qwen Research License](https://huggingface.co/Qwen/Qwen2.5-3B-Instruct-GGUF/blob/7dabda4d13d513e3e842b20f0d435c732f172cbe/LICENSE) grants use “FOR NON-COMMERCIAL PURPOSES ONLY.” Commercial use requires a separate licence. |
| [Phi-4-mini Instruct](https://huggingface.co/bartowski/microsoft_Phi-4-mini-instruct-GGUF/tree/7ff82c2aaa4dde30121698a973765f39be5288c0) `7ff82c2aaa4dde30121698a973765f39be5288c0` | `microsoft_Phi-4-mini-instruct-Q4_K_M.gguf` | 2,491,874,688 | The quantizer card identifies `base_model: microsoft/Phi-4-mini-instruct` and omits a separate licence field. [Microsoft's source licence](https://huggingface.co/microsoft/Phi-4-mini-instruct/blob/cfbefacb99257ffa30c83adab238a50856ac3083/LICENSE) is MIT. Preserve the upstream notice on redistribution. |
| [Gemma 3 4B IT](https://huggingface.co/ggml-org/gemma-3-4b-it-GGUF/tree/d0976223747697cb51e056d85c532013931fe52e) `d0976223747697cb51e056d85c532013931fe52e` | `gemma-3-4b-it-Q4_K_M.gguf` | 2,489,757,856 | Hub card `gemma`. [Gemma terms](https://ai.google.dev/gemma/terms) require redistribution notices and incorporating use restrictions. The official Google Q4_0 artifact returned HTTP 401 without credentials, so this benchmark uses the ggml-org conversion. |
| [Qwen2.5 7B Instruct](https://huggingface.co/Qwen/Qwen2.5-7B-Instruct-GGUF/tree/bb5d59e06d9551d752d08b292a50eb208b07ab1f) `bb5d59e06d9551d752d08b292a50eb208b07ab1f` | `qwen2.5-7b-instruct-q4_k_m-00001-of-00002.gguf` + `qwen2.5-7b-instruct-q4_k_m-00002-of-00002.gguf` | 4,683,073,632 (3,993,201,344 + 689,872,288) | Hub card `apache-2.0`. Both shards are required. |

[Microsoft's Windows AI documentation](https://learn.microsoft.com/en-us/windows/ai/apis) lists Phi Silica on Copilot+ NPUs and some supported GPUs, with no CPU support. An NPU path could be optional on eligible PCs. It cannot provide the CPU-only default for this target.

## Method

Use [llama.cpp b11146](https://github.com/ggml-org/llama.cpp/releases/tag/b11146) as the evaluation harness. Both platforms use the GGUF chat template, 8,192-token context, one slot, no context shifting or prompt cache, greedy decoding and a 768-token reply cap. A truncated reply is a failure. The scorer exercises the same 5,000-character map/reduce budget as the Mac's on-device route, with the line-based protocol. The model process runs locally on each host. The scorer reaches the Windows VM's loopback listener through an SSH tunnel over the private VM connection. The only model inputs are existing synthetic corpus fixtures and the synthetic long transcript.

On the Apple M3 MacBook Air (16 GiB, six inference threads), offload all layers to Metal. On the Windows VM (QEMU x64, 16 visible CPUs, 10 GiB RAM), use eight CPU threads and no GPU layers. Windows VM measurements include the private tunnel overhead. They do not estimate 5900X timings. The app's installed LLamaSharp 0.30.0 builds llama.cpp at `c0c7e147e7efa6c5858754b47259ba4880f8a906` and its cleanup helper uses a cleanup-specific template and 2,048-token context. This benchmark measures the same inference engine family with a different build, template and context. Windows golden tests check port behavior separately.

From the repository root, build with `app/tools/build.sh measure_meeting_notes`, then start `llama-server -m FILE -ngl 99 -c 8192 -np 1 -t 6 --host 127.0.0.1 --port 18085 --no-context-shift --no-cache-prompt`. Run the resulting tool as `measure_meeting_notes llama --30-minute` for corpus and the long sample, then `MMN_CORPUS=app/meeting-notes-heldout.json measure_meeting_notes llama` for heldout. For Windows use `-ngl 0 -t 8` and set `MMN_LLAMA_PORT` to the local forwarded port. The 30-minute workload repeats synthetic corpus entries to about 4,500 words with timestamps spread over 1,800 seconds. Time covers generation and grounding after the server is ready. Each long run is a single sample. Mac peak RAM is maximum resident set size from `/usr/bin/time -l`; VM peak RAM is the server's `PeakWorkingSet64`. These process measurements have different accounting for shared and GPU memory and should be compared within a platform.

The low-end CPU profile uses four llama.cpp threads, one 4,096-token context slot, and Windows Below Normal process priority. The smaller context bounds KV-cache allocation. It does not cap total RAM or reproduce a four-core machine, since the VM still exposes 16 logical CPUs. The concurrent probe times a fixed PowerShell arithmetic loop after a short warm-up, excluding SSH and interpreter startup, at normal priority. Compare its idle and loaded median and p95 to assess foreground CPU contention. This probe does not exercise microphone capture, paging on a smaller machine, UI input latency, or thermal throttling.

The 60-minute workload uses roughly 9,000 synthetic words across 3,600 seconds. Use `--30-minute --60-minute` to time both workloads in the same run. Set `MMN_LLAMA_MAX_TOKENS=1200` for the higher reply-budget sensitivity check. Set `MMN_LLAMA_BOUNDED=1` to ask for up to 8 points, 10 decisions and 12 actions in each extraction and merge. These are prompt instructions in the scorer. Apple uses schema limits. Sample inferences run in sequence after loading the weights. The writer generates fresh nonce tags, so the prompt tokens can vary between otherwise identical runs.

## Apple Intelligence reference

[Historical W7b evidence](https://github.com/theaakashb/Murmur/blob/7ea84769cc0490a1bc1e9cc58706ce0d70d9ce53/PRD.md#1110-acceptance-evidence) records aggregate scores, including zero invented facts on both sets, one unsupported item across 22 development meetings, and none in nine heldout meetings. It reports 482 seconds for the historical one-hour synthetic workload. No saved per-item Apple output was found in the benchmark materials. The table below uses a fresh structured Apple Intelligence run on both sets with the current scorer. The historical one-hour result used different timestamp spacing and is a reference only.

## Quality results

Unsupported is shown as count (per completed meeting). The miss denominators reflect completed cases. Median and maximum are per completed meeting in seconds.

| Runtime / model | Set | Failed / attempted | Unsupported | Invented | Decisions missed | Actions missed | Median / max |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Apple Intelligence | Corpus | 0 / 22 | 2 (0.09) | 0 | 7 / 21 | 6 / 30 | 5.5 / 10.8 |
| Apple Intelligence | Heldout | 0 / 9 | 0 (0.00) | 0 | 3 / 8 | 1 / 11 | 4.3 / 5.6 |
| Metal / Vedu | Corpus | 6 / 22 | 3 (0.19) | 0 | 14 / 16 | 13 / 20 | 2.4 / 4.1 |
| Metal / Vedu | Heldout | 0 / 9 | 1 (0.11) | 0 | 4 / 8 | 3 / 11 | 2.0 / 3.0 |
| Metal / Qwen2.5 3B | Corpus | 1 / 22 | 2 (0.10) | 0 | 12 / 20 | 22 / 29 | 4.7 / 6.6 |
| Metal / Qwen2.5 3B | Heldout | 2 / 9 | 0 (0.00) | 0 | 4 / 6 | 7 / 9 | 6.0 / 7.4 |
| Metal / Phi-4-mini | Corpus | 0 / 22 | 1 (0.05) | 0 | 10 / 21 | 22 / 30 | 6.6 / 8.1 |
| Metal / Phi-4-mini | Heldout | 0 / 9 | 0 (0.00) | 0 | 4 / 8 | 4 / 11 | 6.6 / 9.0 |
| Metal / Gemma 3 4B | Corpus | 0 / 22 | 2 (0.09) | 0 | 8 / 21 | 11 / 30 | 8.0 / 15.6 |
| Metal / Gemma 3 4B | Heldout | 0 / 9 | 0 (0.00) | 0 | 3 / 8 | 3 / 11 | 8.4 / 11.1 |
| Metal / Qwen2.5 7B | Corpus | 0 / 22 | 1 (0.05) | 0 | 6 / 21 | 16 / 30 | 13.9 / 23.8 |
| Metal / Qwen2.5 7B | Heldout | 0 / 9 | 0 (0.00) | 0 | 2 / 8 | 3 / 11 | 13.0 / 14.9 |
| VM CPU / Vedu | Corpus | 5 / 22 | 2 (0.12) | 0 | 15 / 17 | 11 / 20 | 12.3 / 28.6 |
| VM CPU / Vedu | Heldout | 0 / 9 | 1 (0.11) | 0 | 4 / 8 | 3 / 11 | 11.5 / 13.0 |

## End-to-end workloads and responsiveness

A failed time is elapsed time to rejection. It does not measure time to usable notes. Peak RSS or working set is the maximum observed over the process's benchmark cases, not app-wide memory. GB is decimal.

| Runtime / model | 30-minute workload | 60-minute workload | Peak process memory |
| --- | ---: | ---: | ---: |
| Metal / Vedu | Failed at 26 s | — | 1.79 GB |
| Metal / Qwen2.5 3B | Failed at 192 s | — | 3.20 GB |
| Metal / Phi-4-mini | Failed at 65 s | Failed at 196 s | 5.11 GB |
| Metal / Gemma 3 4B | Failed at 77 s | Failed at 48 s | 7.32 GB |
| Metal / Qwen2.5 7B | Completed in 349 s | Failed at 205 s | 5.76 GB |
| VM CPU, 8 threads / Vedu | Failed at 187 s | — | 4.38 GB |
| VM laptop proxy, 4 threads / Vedu | Failed at 94 s | Failed at 201 s | 1.46 GB |

The Qwen 7B 30-minute run completed seven extraction requests and one merge. Grounding retained 10 points, one decision and five actions and dropped 32 items. Long fixtures measure completion and resources. They have no aggregate quality labels.

The failed Metal jobs for Phi, Gemma and Qwen 7B exhausted the 768-token allowance during extraction. The failing calls used 1,945–2,657 prompt tokens in an 8,192-token slot, with no context overflows. The line protocol requests as many items as needed and has no per-item length or citation limit. The scorer rejects a reply that did not finish normally, before the writer parses or grounds it. Relaxing that check could hide incomplete notes. A bounded extraction prompt or splitting and retrying on output exhaustion could change completion. A bounded merge would also be needed before treating that recovery as reliable. Any candidate change should recheck decision and action recall as well as completion.

The Vedu four-thread process ran at Below Normal priority. A normal-priority PowerShell loop summed 150,000 integers, timed after a 10,000-iteration warm-up. Idle p50/p95 was 212.50/244.06 ms (10 samples). Loaded p50/p95 was 250.84/348.09 ms (62 samples across both jobs), an 18% increase at the median and 43% at p95. The probe completed throughout the sampled interval. This shows measurable contention with some CPU capacity left on this VM. It provides no measurement of responsiveness while dictating.

The VM started with 6.40 GB free on C:. The Qwen 3B and Phi downloads encountered incomplete ranges. On a subsequent Phi retry, system free space fell to 2.37 GB while the full 2.49 GB file was still pending. Downloads for Phi and Gemma were stopped to preserve the disk floor. Qwen 7B was blocked before download because both shards could not fit with the required reserve. The staging folder was removed, the pre-existing shared Vedu stayed in place, and C: reported 3.35 GB free at cleanup.

## Hardware policy to validate

- Run one notes job after the meeting saves. Use a low-priority worker and defer or pause at request boundaries when dictation, a model download, or a new recording needs resources. Expose progress by completed extraction and merge stages, with Cancel and Retry. A failed, cancelled or deferred job leaves the saved transcript usable.
- Check usable memory at run time. For a CPU pilot, start with at least four physical cores and 16 GiB installed RAM, and require measured peak working set plus at least 2 GiB available headroom. A four-thread cap controls contention. A 4,096-token slot limits the KV cache and may require more context splits. Eight GiB systems and a machine already under memory pressure are outside the verified range.
- For a GPU pilot, use the same notes contract via LLamaSharp Vulkan. Require measured device allocations plus headroom for the active display and other app work. A 4.68 GB model needs headroom beyond the weight file for KV state, scratch buffers and the driver. The Mac unified-memory numbers cannot establish discrete VRAM requirements or RTX 3080 latency.
- Offer Murmur Cloud notes as an explicit choice when the machine falls below the checked memory or CPU threshold, a prior local job failed, or local processing is estimated to take longer than the user wants to wait. Disclose what is sent and the service's retention before first use. A failed or slow local run must not trigger a network fallback.
