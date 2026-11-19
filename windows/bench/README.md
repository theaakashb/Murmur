# Speech benchmark scripts

Measures the Windows speech helper (`windows/src/Murmur.Speech`) against the Mac's Core ML numbers. Results and limits: [RESULTS.md](RESULTS.md). No audio or transcript outside the synthetic fixtures is read or written, and the WAVs are never committed.

| File | What it does |
|---|---|
| `AsrBench/` | Console app (references `Murmur.Core`). For each fixture it runs the one-shot helper (`--single`) through `ModelHelper.RunOnceAsync`, then two passes through the app's own `WarmRunner` (`--serve`). Writes JSON lines: an environment line (CPU, RAM, OS), then one line per clip and mode. |
| `score.py` | Reads those files and the Mac baseline (`~/murmur-m3/bench/mac-asr.jsonl`) and prints the Markdown tables. WER comes from `module-one/models/benchmark.py` (`words`, `edits`), so the normalization is the one MODEL_BENCHMARK.md uses. |
| `hotwords-phrases.tsv`, `hotwords-make-audio.sh`, `hotwords_spike.py` | The dictionary (hotwords) spike. The shell script (macOS `say`) writes the made-up-name WAVs and a vocabulary into a scratch folder; the Python script runs one `--serve` helper per configuration. |
| `rss_check.py` | Resident memory of the warm helper, with and without dictionary support. |

## Reproduce

Model folders are the pinned downloads (`encoder.int8.onnx`, `decoder.int8.onnx`, `joiner.int8.onnx`, `tokens.txt`). Fixtures: `windows/harness/fixtures/generate.sh` (macOS).

```sh
# Mac (osx-arm64)
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet publish windows/src/Murmur.Speech -c Release -r osx-arm64 --self-contained true -o /tmp/helper-mac
dotnet run --project windows/bench/AsrBench -c Release -- --helper /tmp/helper-mac/MurmurSpeech \
  --models ~/murmur-m3/dl/sherpa-onnx-nemo-parakeet-unified-en-0.6b-int8-non-streaming-8c3a10f \
  --fixtures windows/harness/fixtures --out /tmp/mac-onnx.jsonl --label mac-onnx
```

```sh
# Windows (win-x64): publish on the Mac, copy the folder, build and run the bench on Windows
dotnet publish windows/src/Murmur.Speech -c Release -r win-x64 --self-contained true -o /tmp/helper-win
# on the Windows machine, from windows\ :
dotnet run --project bench\AsrBench -c Release -- --helper <helper-win>\MurmurSpeech.exe --models <model folder> --fixtures <fixtures folder> --out vm-onnx.jsonl --label vm-onnx
```

```sh
# Tables (Mac)
python3 -I windows/bench/score.py --manifest windows/harness/fixtures/manifest.json \
  --coreml ~/murmur-m3/bench/mac-asr.jsonl mac-onnx=/tmp/mac-onnx.jsonl vm-onnx=vm-onnx.jsonl
```

Dictionary run: add `--vocabulary FILE --lexicon WORDLIST` to the bench command (the vocabulary is FluidAudio's JSON, `{"terms":[{"text":"…","aliases":["…"]}]}`).

Hotwords spike:

```sh
windows/bench/hotwords-make-audio.sh /tmp/spike
python3 -I windows/bench/hotwords_spike.py --helper /tmp/helper-mac/MurmurSpeech --models <model folder> \
  --spike-dir /tmp/spike --fixtures windows/harness/fixtures --lexicon /usr/share/dict/words --scores 1.5,3,5
python3 -I windows/bench/rss_check.py /tmp/helper-mac/MurmurSpeech <model folder> windows/harness/fixtures/long-plan.wav \
  --vocabulary /tmp/spike/vocabulary.json --lexicon /usr/share/dict/words
```

`MURMUR_ASR_THREADS` sets the helper's thread count (default `min(4, logical CPUs)`); `MURMUR_HOTWORDS_SCORE` sets the hotword score (default 1.5); `MURMUR_ASR_UNFILTERED=1` answers with the boosted text before `BoostFilter`, for the spike only.

## Local route in the exit harness

`windows\harness\run-exit.ps1 -LocalAsr -ModelDir <model folder> -SpeechHelper <MurmurSpeech.exe>` writes `transcriptionRoute: local` and `modelDirectory`, points the app at the helper with `MURMUR_SPEECH_HELPER`, keeps the fake cleanup provider, and requires that the provider saw no transcription request. The harness needs the fixture WAVs in `windows\harness\fixtures`.
