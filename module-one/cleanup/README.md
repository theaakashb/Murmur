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
2. `windows/bench/cleanup/patch_gguf_layers.py GGUF_PY_DIR IN OUT` rewrites the header to 24 layers. The converter writes
   `qwen35.block_count = 25` and `nextn_predict_layers = 1` because the config lists one multi-token-prediction layer,
   but the checkpoint has no such tensors. The llama.cpp bundled with LLamaSharp 0.30.0 refuses that file
   (`missing tensor 'blk.24.attn_norm.weight'`, then a wrong-length `recurrent_layers` array). The patch sets
   `block_count = 24`, `nextn_predict_layers = 0`, drops the last `recurrent_layers` entry and copies every tensor byte
   for byte (the script re-reads the result and compares all tensors). Murmur never uses the multi-token-prediction layer.
   Ship the patched files; the unpatched ones do not load in the app.

## Staging for Hugging Face

`~/murmur-m3/hf-stage/vedu-scribe-0.8b-gguf/` (outside the repo, never committed) holds what would be uploaded: the
patched GGUF, `README.md` (model card: base model, modification statement, conversion steps, SHA-256, prompt format)
and `LICENSE` (Apache-2.0 text, fetched from apache.org because the source checkpoint ships none). Nothing is uploaded
until the owner says so; after upload, pin the repository revision and SHA-256 in `artifact-pins.json`.

## Checking a build

- `windows/bench/cleanup/tokenizer_parity.py`: token IDs of the MLX tokenizer against the GGUF tokenizer.
- `windows/bench/cleanup/run_corpus.py` then `windows/bench/cleanup/CorpusReport`: the corpus through any helper that
  speaks the protocol, compared with the MLX runner's output, with the strict guard verdict for each.
