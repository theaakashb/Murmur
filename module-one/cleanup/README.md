# Local cleanup model

Vedu Scribe 0.8B (`heyvedu/vedu-scribe-0.8b` at `7dced5841c58d7d6e0118c4c123f222b35057871`, Apache-2.0, a fine-tune of
`Qwen/Qwen3.5-0.8B`) is the "Local model" cleanup on both platforms.

- **Mac:** the pinned MLX checkpoint, run by `Runner/` (`LocalCleanupRunner`).
- **Windows:** a GGUF of the same weights, run by `windows/src/Murmur.Cleanup` (LLamaSharp, llama.cpp CPU backend).
  The helper speaks the same one-JSON-line protocol and builds the same prompt. Measured agreement with the Mac is in
  `CLEANUP_BENCHMARK.md`, "Local model on Windows (GGUF via llama.cpp) vs Mac (MLX)".

## Making the GGUF

No full-precision Vedu Scribe checkpoint is published, only the MLX mixed 4/8-bit one, so the GGUF is a modified work:
the MLX values dequantized, renamed and repacked. It is not the original file.

1. `convert_gguf.py MODEL_DIR LLAMA_CPP_DIR OUT_DIR` (mlx-lm 0.32.2, torch 2.15.0, transformers, llama.cpp
   `c7fc5094179bf8a9c73c18593db714f8da41a402`; the script checks both pins) dequantizes the checkpoint to bfloat16,
   undoes mlx-lm's Qwen3.5 load-time changes and runs llama.cpp's `convert_hf_to_gguf.py` for F16 and Q8_0. Its output
   is reproducible: all 320 Q8_0 tensors equal those of the third-party `sumitsah35/vedu-scribe-0.8b-gguf@4b72e79`.
2. `module-one/cleanup/patch_gguf_layers.py GGUF_PY_DIR IN OUT`, which `convert_gguf.py` runs as its last step, rewrites the header to 24 layers. The converter writes
   `qwen35.block_count = 25` and `nextn_predict_layers = 1` because the config lists one multi-token-prediction layer,
   but the checkpoint has no such tensors. The llama.cpp bundled with LLamaSharp 0.30.0 refuses that file
   (`missing tensor 'blk.24.attn_norm.weight'`, then a wrong-length `recurrent_layers` array). The patch sets
   `block_count = 24`, `nextn_predict_layers = 0`, drops the last `recurrent_layers` entry and copies every tensor byte
   for byte (the script re-reads the result and compares all tensors). Murmur never uses the multi-token-prediction layer.
   `convert_gguf.py` writes only the patched files: Q8_0 SHA-256 `6859e09e41502d2c1665b9462b2f9f37c7e485ebddd14185813f22a89554c708`.

## Published copy

The Q8_0 file is published at https://huggingface.co/clouddevaakash/vedu-scribe-0.8b-gguf, revision `edf275972c822fb7787226a89afa4282feb74ec4`.
- It is 811,843,296 bytes, SHA-256 `6859e09e41502d2c1665b9462b2f9f37c7e485ebddd14185813f22a89554c708`.
- The repository also holds a model card (base model, modification statement, conversion steps, prompt format) and the Apache-2.0 `LICENSE`.
- The Windows app pins it in `windows/shared/windows-models.json`. The Mac keeps running the MLX checkpoint from `artifact-pins.json`.

## Checking a build

- `windows/bench/cleanup/tokenizer_parity.py`: token IDs of the MLX tokenizer against the GGUF tokenizer.
- `windows/bench/cleanup/run_corpus.py` then `windows/bench/cleanup/CorpusReport`: the corpus through any helper that
  speaks the protocol, compared with the MLX runner's output, with the strict guard verdict for each.
