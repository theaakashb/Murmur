# Local meeting notes benchmark

Phase 1 evaluates user-downloaded GGUFs for offline notes on Windows. The target PC has a Ryzen 9 5900X, 32 GB RAM and an RTX 3080. The Windows VM and M3 CPU-only runs serve as separate laptop proxies.

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
| M3 CPU-only proxy, 4 threads / Qwen2.5 7B, 1,200 tokens | Completed in 1,635 s | Cancelled during extraction | 6.34 GB |

The Qwen 7B 30-minute run completed seven extraction requests and one merge. Grounding retained 10 points, one decision and five actions and dropped 32 items. Long fixtures measure completion and resources. They have no aggregate quality labels.

The failed Metal jobs for Phi, Gemma and Qwen 7B exhausted the 768-token allowance during extraction. The failing calls used 1,945–2,657 prompt tokens in an 8,192-token slot, with no context overflows. The line protocol requests as many items as needed and has no per-item length or citation limit. The scorer rejects a reply that did not finish normally, before the writer parses or grounds it. Relaxing that check could hide incomplete notes. A bounded extraction prompt or splitting and retrying on output exhaustion could change completion. A bounded merge would also be needed before treating that recovery as reliable. Any candidate change should recheck decision and action recall as well as completion.

Qwen 7B at 1,200 tokens completed 30 minutes in 351 s and retained two points, three decisions and zero actions. Its 60-minute workload exhausted all 1,200 tokens on extraction request four, the same request that failed at 768. It took 293 s to fail. Peak process RSS for the sensitivity run was 5.46 GB.

The M3 CPU-only run used a 4,096-token context, nice +10 and zero GPU layers. It completed eight requests for 30 minutes and retained one point and one action, with 45 drops. The 60-minute job was stopped in its fifth extraction after about 14 minutes to give the Metal reruns priority. A normal-priority Python arithmetic probe returned idle p50/p95 of 18.38/19.18 ms (10 samples), and loaded p50/p95 of 35.75/60.57 ms (2,410 samples across the completed and interrupted workloads). The probe measures CPU contention on this Mac. Its values cannot be compared directly with the VM's PowerShell probe.

The Vedu four-thread process ran at Below Normal priority. A normal-priority PowerShell loop summed 150,000 integers, timed after a 10,000-iteration warm-up. Idle p50/p95 was 212.50/244.06 ms (10 samples). Loaded p50/p95 was 250.84/348.09 ms (62 samples across both jobs), an 18% increase at the median and 43% at p95. The probe completed throughout the sampled interval. This shows measurable contention with some CPU capacity left on this VM. It provides no measurement of responsiveness while dictating.

The VM started with 6.40 GB free on C:. The Qwen 3B and Phi downloads encountered incomplete ranges. On a subsequent Phi retry, system free space fell to 2.37 GB while the full 2.49 GB file was still pending. Downloads for Phi and Gemma were stopped to preserve the disk floor. Qwen 7B was blocked before download because both shards could not fit with the required reserve. The staging folder was removed, the pre-existing shared Vedu stayed in place, and C: reported 3.35 GB free at cleanup.

## Star Labs PC

The temporary Windows harness used the C# writer with its budget set to 5,000 characters for comparison to the Swift scorer. The 30-minute fixture had 4,501 words, 569 lines and seven extraction chunks. The 60-minute fixture had 9,018 words, 1,156 lines and 14 chunks. The official b11146 x64 CPU and Vulkan archives matched their SHA-256 digests. The harness requested 1,200 reply tokens, greedy decoding, one 8,192-token slot and no prompt cache or context shifting. CPU used 12 threads, with zero GPU layers. Vulkan used six host threads and offloaded every model layer to the RTX 3080. Offload and buffer sizes were checked in separate load-only runs. Peak working set below is for the server process.

| Model / backend | 30-minute workload | 60-minute workload | Peak working set |
| --- | --- | --- | ---: |
| Phi-4-mini / 5900X CPU | Length cap at 122 s, extraction 1 | Length cap at 148 s, extraction 2 | 5.33 GB |
| Phi-4-mini / RTX 3080 Vulkan | Length cap at 25 s, extraction 1 | Unparseable reply at 4 s, extraction 1 | 3.43 GB |
| Gemma 3 4B / 5900X CPU | Length cap at 136 s, extraction 2 | Length cap at 238 s, extraction 4 | 6.44 GB |
| Gemma 3 4B / RTX 3080 Vulkan | Completed in 58 s | Length cap at 45 s, extraction 12 | 11.45 GB |

Gemma Vulkan took seven extraction and three merge requests to complete 30 minutes. The writer kept 18 points, 17 decisions and 30 actions, and dropped 70 items. This repeated fixture has no recall labels. The PC runs in the table had zero context overflows. Total GPU memory used rose from 1,210 MiB before the runs to peaks of 4,752 MiB for Phi and 4,386 MiB for Gemma.

| PC responsiveness probe (150,000 integer additions) | Idle p50 / p95 | Loaded p50 / p95 |
| --- | ---: | ---: |
| Phi CPU | 159 / 222 ms | 251 / 283 ms |
| Gemma CPU | 209 / 234 ms | 221 / 274 ms |
| Gemma Vulkan | 130 / 155 ms | 195 / 243 ms |

Processes from a separate GPU benchmark were present throughout the Phi runs and during part of the Gemma CPU run. Their activity during those samples was unknown. They were absent during the Gemma Vulkan run. Pre-run GPU utilisation was 0–1% for Phi and Gemma.

## Hardware policy to validate

- Run one notes job after the meeting saves. Use a low-priority worker and defer or pause at request boundaries when dictation, a model download, or a new recording needs resources. Expose progress by completed extraction and merge stages, with Cancel and Retry. A failed, cancelled or deferred job leaves the saved transcript usable.
- Check usable memory at run time. For a CPU pilot, start with at least four physical cores and 16 GiB installed RAM, and require measured peak working set plus at least 2 GiB available headroom. A four-thread cap controls contention. A 4,096-token slot limits the KV cache and may require more context splits. Eight GiB systems and a machine already under memory pressure are outside the verified range.
- For a GPU pilot, use the same notes contract via LLamaSharp Vulkan. Require measured device allocations plus headroom for the active display and other app work. A 4.68 GB model needs headroom beyond the weight file for KV state, scratch buffers and the driver. The Mac unified-memory numbers cannot establish discrete VRAM requirements or RTX 3080 latency.
- Offer Murmur Cloud notes as an explicit choice when the machine falls below the checked memory or CPU threshold, a prior local job failed, or local processing is estimated to take longer than the user wants to wait. Disclose what is sent and the service's retention before first use. A failed or slow local run must not trigger a network fallback.

## Status and interim recommendation (updated 23:30Z)

Gemma 3 4B on Metal at a 1,200-token reply cap completed the 30-minute workload in 349 s, with 28 points, 7 decisions and 31 actions kept and 61 dropped (10 requests, no context overflows). Its 60-minute run was interrupted when the Mac went offline.

Unfinished, not failures of any model: Metal 60-minute Gemma at 1,200 tokens; Phi at 1,200 tokens; bounded (item-limit) 768-token runs for Gemma, Phi and Qwen 7B; Qwen M3 CPU 60-minute (cancelled at extraction five); VM Phi 60-minute (Phi 30-minute on the VM low-end profile timed out at 601 s) and VM Gemma; PC Qwen 7B CPU and Vulkan, bounded and quality runs (still running on the PC). The VM task files and process were removed; C: had 4.90 GB free afterwards. The earlier queue stopped unexpectedly around 23:24Z, so these runs have no data, not a negative result.

Interim view, pending those runs: no CPU-only default and no low-RAM fallback model (the M3 CPU proxy needed 1,635 s for 30 minutes with Qwen 7B and kept almost nothing; 5900X CPU runs for Phi and Gemma hit the reply cap). Gemma 3 4B on a discrete GPU is the only pilot candidate (RTX 3080 Vulkan: 30 minutes in 58 s, 60 minutes blocked by the cap). Nothing is pinned or shipped. Whether item limits fix long meetings is open.

## Star Labs PC: full matrix (2026-11-21)

PC agent 76b8269f finished the remaining PC cells. Setup matches the "Star Labs PC" section above: 1,200 reply tokens, one 8,192-token slot, CPU on 12 threads, Vulkan with every layer on the RTX 3080. The bench processes ran at Below Normal priority. "Bounded" sets `MMN_LLAMA_BOUNDED=1`, which caps each extraction and merge at 8 points, 10 decisions and 12 actions. F means the run failed, followed by the seconds until it was rejected. A completed time is the time to notes. GPU is the peak total MiB in use; the baseline before the runs was about 1,210 MiB. Each cell is a single sample.

| Cell | 30-minute | 60-minute | Peak working set | GPU MiB |
| --- | ---: | ---: | ---: | ---: |
| Phi-4-mini / CPU | F 122 | F 148 | 5.33 GB | — |
| Phi-4-mini / Vulkan | F 25 | F 4 | 3.43 GB | 4,752 |
| Gemma 3 4B / CPU | F 136 | F 238 | 6.44 GB | — |
| Gemma 3 4B / Vulkan | 58 | F 45 | 11.45 GB | 4,386 |
| Qwen2.5 7B / CPU | 462 | F 610 | 9.74 GB | — |
| Qwen2.5 7B / Vulkan | 23 | F 17 | 6.22 GB | 6,294 |
| Bounded Phi-4-mini / CPU | F 51 | F 170 | 5.27 GB | — |
| Bounded Phi-4-mini / Vulkan | F 7 | F 17 | 4.13 GB | 4,755 |
| Bounded Gemma 3 4B / CPU (rerun) | 512 | F 689 | 12.34 GB | — |
| Bounded Gemma 3 4B / Vulkan | F 37 | F 56 | 11.34 GB | 4,603 |
| Bounded Qwen2.5 7B / CPU | 561 | F 382 | 9.88 GB | — |
| Bounded Qwen2.5 7B / Vulkan | 26 | F 55 | 7.89 GB | 6,803 |

- **Failure causes:** almost every failure was one extraction or merge request that used the full 1,200-token reply cap. Two cells returned an unparseable reply instead: bounded Phi CPU at 30 minutes and Phi Vulkan at 60 minutes. No cell overflowed its context.
- **No 60-minute run completed in any cell, bounded or not.** Item limits did not fix long meetings at a 1,200-token cap. Bounded Qwen 7B CPU failed on extraction 4 at 60 minutes, and bounded Gemma CPU on request 13 of 14.
- **Bounded Gemma CPU, 30 minutes:** 10 requests (7 extraction, 3 merge); 15 points, 14 decisions and 12 actions kept; 83 dropped. An earlier run of this cell failed on a merge length cap; the rerun completed.
- **Bounded Qwen 7B CPU, 30 minutes:** 8 requests; 3 points, 1 decision and 0 actions kept; 50 dropped.
- **CPU throughput on the 5900X, bounded (estimated):** about 4.7 tokens per second for Qwen 7B and about 10 for Gemma 3 4B.
- **Caveat:** processes from a separate GPU benchmark overlapped the Phi CPU, Phi Vulkan and part of the Gemma CPU runs, so those timings may be affected.
- **Not run:** the quality set (scoring generated items against the corpus on the PC). It was never implemented in the PC harness.

The interim recommendation stands. There is no CPU-only default and no low-RAM fallback: the CPU cells that completed took 462–561 s for 30 minutes, and no 60-minute run completed. Gemma 3 4B on a discrete GPU stays the pilot candidate. Qwen2.5 7B on Vulkan was the fastest to complete 30 minutes (23 s). Long meetings need a fix for output exhaustion, such as splitting and retrying when a reply hits the cap, before any model can be called reliable. Nothing is pinned; the model choice goes to the user.

## Long-meeting fix: item caps, split on output exhaustion (2026-11-20, Star Labs PC)

Prototype in `windows/bench/notes/harness2` (a bench copy of the C# writer; `Murmur.Core` is untouched). Setup as above: 1,200 reply tokens, one 8,192-token slot, 5,000-character chunks, Below Normal priority, single samples, no context overflows anywhere. Vulkan offloads every layer, six host threads; CPU uses 12 threads. A separate build ran briefly beside the first Gemma run. GPU MiB is the peak total in use (baseline about 1,300).

**Diagnosis from the existing 12 cells.** Every length failure but two was one request using all 1,200 tokens, and extraction was the usual one: 16 extraction against 1 merge in the unbounded cells. Failing extractions had ordinary prompts (2,450–2,720 tokens) while their siblings in the same run answered in 190–700 tokens. The cap is hit by one runaway reply, not by a large input. The one merge failure (bounded Gemma Vulkan, 30 minutes) had a 1,955-token prompt and a 1,200-token reply. Merges do grow with accumulated items: unbounded Gemma Vulkan merge replies were 1,188, 447 and 1,133 tokens. The bounded prompt alone did not remove runaway replies, because the model can ignore a prompt limit. Two failures were unparseable replies (Phi), which splitting would also address.

**Variants.** "Cap" adds the Mac limits (8 points, 10 decisions, 12 actions) to the prompt and truncates every parsed extraction reply, every merge reply and the grounded final draft to them, keeping the first items. "Split" retries once, on half the input, when a reply hits the token cap (extraction: half the lines; merge: half the drafts; a lone draft is passed through), and fails if a half also hits the cap. "Fan3" merges at most three drafts per request.

| Cell | 30-minute | 60-minute | Requests (30 / 60) | Peak working set | GPU MiB |
| --- | ---: | ---: | ---: | ---: | ---: |
| Gemma / Vulkan, cap + split | 41 s | 76 s | 10 / 18 | 12.05 GB | 4,520 |
| Gemma / Vulkan, cap + split + fan3 | 43 s | 90 s | 10 / 22 | 12.18 GB | 4,509 |
| Gemma / Vulkan, split only | 37 s | F 124 s (merge) | 10 / 28 | 12.07 GB | 4,509 |
| Qwen 7B / Vulkan, cap + split | 24 s | 56 s | 8 / 17 | 8.80 GB | 6,780 |
| Qwen 7B / Vulkan, cap + split + fan3 | 31 s | 89 s | 10 / 24 | 9.55 GB | 6,699 |
| Qwen 7B / Vulkan, split only | 20 s | 59 s | 8 / 19 | 8.83 GB | 6,698 |
| Gemma / CPU, cap + split | 629 s | 1,219 s | 12 / 21 | 13.25 GB | — |

Kept (points/decisions/actions) and dropped, 30 / 60 minutes: Gemma cap + split 8/9/12, 69 and 8/10/12, 141; Gemma fan3 8/10/12, 74 and 8/10/11, 147; Gemma split only 25/10/23, 60 and failed; Qwen cap + split 0/2/6, 46 and 8/0/7, 98; Qwen fan3 6/1/2, 60 and 8/1/11, 81; Qwen split only 1/1/1, 38 and 14/1/14, 69; Gemma CPU 8/10/12, 63 and 8/10/12, 131. Items cut by the cap after parsing: 43–125 per Gemma run, 0–19 per Qwen run. Output exhaustion with splitting enabled: Gemma Vulkan cap + split had none; Gemma CPU cap + split split once at 30 minutes and twice at 60; Gemma split only had five cap hits and four splits and still failed at 60 minutes when a merge of two drafts could not be split; Qwen fan3 and split only each recovered one extraction or merge.

- **Cap plus split completed all seven cells**, including both 60-minute Vulkan runs and the Gemma CPU run. Split alone did not complete Gemma at 60 minutes, so the cap is needed for Gemma. Qwen completed with either.
- **Fan3 did not help.** It added requests and time with no reduction in cap hits.
- **The cap trims heavily on Gemma.** It generates more items than the limits allow, so a long meeting ends with about 8/10/12 items from a pool that is truncated at every merge level. Truncation keeps the first items, not the best.
- **Qwen kept few decisions and actions at 30 and 60 minutes.** Gemma kept more.

**Quality (22-meeting corpus and 9-meeting heldout, Vulkan, C# port of the Swift scorer, 1,200 tokens, 5,000-character chunks).** Unsupported is count; invented is names or numbers; misses are labelled decisions and actions. Failed meetings: 0 in every row.

| Variant | Set | Unsupported | Invented | Decisions missed | Actions missed |
| --- | --- | ---: | ---: | ---: | ---: |
| Gemma baseline (no cap, no split) | Corpus | 3 | 0 | 11 / 21 | 10 / 30 |
| Gemma cap + split | Corpus | 4 | 0 | 10 / 21 | 10 / 30 |
| Gemma baseline | Heldout | 0 | 0 | 3 / 8 | 3 / 11 |
| Gemma cap + split | Heldout | 0 | 0 | 4 / 8 | 3 / 11 |
| Qwen 7B baseline | Corpus | 0 | 0 | 6 / 21 | 21 / 30 |
| Qwen 7B cap + split | Corpus | 1 | 0 | 8 / 21 | 18 / 30 |
| Qwen 7B baseline | Heldout | 0 | 0 | 3 / 8 | 6 / 11 |
| Qwen 7B cap + split | Heldout | 0 | 0 | 4 / 8 | 7 / 11 |

Gemma cap + split: 0.18 unsupported per corpus meeting, within the 0.20 bar; zero invented. Qwen 7B cap + split: 0.05. The short meetings have one chunk, so the cap and split rarely act on them, and the differences are within noise for single samples. These Vulkan runs differ from the earlier Metal rows (Gemma misses 8/21 and 11/30 on the corpus there), so compare within this table. The fixtures give no recall labels for the repeated 30- and 60-minute meetings, where the cap's loss of items cannot be measured.

**Proposed contract change (Mac and Windows writer).**
1. Both extraction and merge replies are capped at 8 points, 10 decisions and 12 actions after parsing, for every route, not only Apple's schema. Truncate after grounding on extraction, and after parsing on merge.
2. When a request stops at the reply cap (finish reason length), split that request's input once and retry each half: extraction by lines, merge by drafts. Treat a second cap hit as a failure, mirroring the one split on context overflow. A merge of two drafts cannot be split, so it fails.
3. Add the line-limit sentence to the line-protocol system prompts for custom routes.
4. Keep the 1,200-token cap. Do not add a fan-out limit.

Cleanup: nothing new was written on C:. Models, binaries and result folders stay under `T:\Paseo Project\Murmur\bench-notes`; run folders are `results\runs2` and `results\quality`.

**Gemma CPU quality and decision (2026-11-21).** Gemma 3 4B with cap + split on the 5900X CPU (12 threads): corpus 0 / 22 failed, 5 unsupported (0.23 per meeting, above the 0.20 bar), 0 invented, decisions missed 9 / 21, actions missed 12 / 30, median 19 s and maximum 28.6 s per meeting. Heldout 0 / 9 failed, 0 unsupported, 0 invented, decisions missed 4 / 8, actions missed 5 / 11, median 17.7 s. The Vulkan scores are above. These are single samples with no cap or split triggered on the short meetings, so the 5-versus-4 gap on the corpus is run-to-run noise from the nonce-varied prompts, not a CPU effect. CPU time to notes for the long workloads is 629 s at 30 minutes and 1,219 s at 60 minutes, so a no-GPU run can take 10 to 20 minutes. The user chose Gemma 3 4B as the Windows default and approved the contract change above. No further NuGet data went to C:; `C:\Users\aakash\AppData\Local\NuGet\v3-cache` does not exist.
