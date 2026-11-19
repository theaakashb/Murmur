#!/usr/bin/env python3
"""Converts the pinned Vedu Scribe MLX checkpoint to GGUF for the Windows app (windows/PLAN.md "Local cleanup model").

    python3 convert_gguf.py MODEL_DIR LLAMA_CPP_DIR OUT_DIR

MODEL_DIR is heyvedu/vedu-scribe-0.8b at the revision in artifact-pins.json, verified first. LLAMA_CPP_DIR is a
llama.cpp checkout at the commit below. Run inside a virtual environment with mlx-lm 0.32.2, torch and transformers.
Writes OUT_DIR/vedu-scribe-0.8b-F16.gguf and -Q8_0.gguf and prints their SHA-256. Makes no network requests.

1. mlx-lm dequantizes the mixed 4/8-bit checkpoint to bfloat16: the values the Mac computes with.
2. mlx-lm's Qwen3.5 load-time changes are undone: Hugging Face tensor names, the depthwise conv1d weight back to
   [C, 1, K], and RMSNorm weights stored by mlx-lm as 1 + w back to w.
3. llama.cpp's converter writes F16 (keeps the dequantized values) and Q8_0 (requantizes them).
"""
import hashlib, os, shutil, subprocess, sys, tempfile

LLAMA_CPP_COMMIT = "c7fc5094179bf8a9c73c18593db714f8da41a402"
MLX_LM_VERSION = "0.32.2"
NORMS = (".input_layernorm.weight", ".post_attention_layernorm.weight", "model.norm.weight", ".q_norm.weight", ".k_norm.weight")


def unsanitize(src, dst):
    from safetensors.torch import load_file, save_file
    weights = load_file(os.path.join(src, "model.safetensors"))
    out = {}
    for key, value in weights.items():
        if "conv1d.weight" in key and value.ndim == 3 and value.shape[-1] == 1:
            value = value.permute(0, 2, 1).contiguous()  # MLX [C, K, 1] -> HF [C, 1, K]
        if key.endswith(NORMS) and value.ndim == 1:
            value = (value.float() - 1.0).to(value.dtype)  # MLX stores 1 + w
        if key.startswith("language_model.model."):
            key = "model.language_model." + key[len("language_model.model."):]
        elif key.startswith("language_model.lm_head"):
            key = "lm_head" + key[len("language_model.lm_head"):]
        out[key] = value
    os.makedirs(dst, exist_ok=True)
    save_file(out, os.path.join(dst, "model.safetensors"), metadata={"format": "pt"})
    for name in os.listdir(src):
        if name not in ("model.safetensors", "model.safetensors.index.json"):
            shutil.copy(os.path.join(src, name), os.path.join(dst, name))
    return len(out)


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def main(model, llama, out):
    import mlx_lm
    if mlx_lm.__version__ != MLX_LM_VERSION:
        sys.exit(f"mlx-lm {MLX_LM_VERSION} required, found {mlx_lm.__version__}")
    head = subprocess.run(["git", "-C", llama, "rev-parse", "HEAD"], capture_output=True, text=True, check=True).stdout.strip()
    if head != LLAMA_CPP_COMMIT:
        sys.exit(f"llama.cpp {LLAMA_CPP_COMMIT} required, found {head}")
    os.makedirs(out, exist_ok=True)
    with tempfile.TemporaryDirectory() as tmp:
        bf16, hf = os.path.join(tmp, "bf16"), os.path.join(tmp, "hf")
        subprocess.run([sys.executable, "-m", "mlx_lm", "convert", "--hf-path", model, "--mlx-path", bf16, "--dequantize"], check=True)
        print(unsanitize(bf16, hf), "tensors")
        env = dict(os.environ, PYTHONPATH=os.path.join(llama, "gguf-py"))
        for kind in ("f16", "q8_0"):
            target = os.path.join(out, f"vedu-scribe-0.8b-{kind.upper()}.gguf")
            subprocess.run([sys.executable, os.path.join(llama, "convert_hf_to_gguf.py"), hf, "--outtype", kind, "--outfile", target], check=True, env=env)
            print(os.path.basename(target), os.path.getsize(target), sha256(target))


if __name__ == "__main__":
    if len(sys.argv) != 4:
        sys.exit(__doc__)
    main(*sys.argv[1:])
