#!/usr/bin/env python3
"""Make a converter-written Vedu Scribe GGUF loadable by the llama.cpp inside LLamaSharp 0.30.0.

Usage: patch_gguf_layers.py LLAMA_CPP_GGUF_PY_DIR IN.gguf OUT.gguf

convert_gguf.py's llama.cpp commit writes qwen35.block_count = 25, nextn_predict_layers = 1 and a 25-entry
qwen35.attention.recurrent_layers, because the config lists one multi-token-prediction layer, but the checkpoint has
no tensors for it (no blk.24.*). llama.cpp c7fc509 tolerates that; the llama.cpp bundled with LLamaSharp 0.30.0 does
not ("missing tensor 'blk.24.attn_norm.weight'", then "recurrent_layers has wrong array length"). This rewrites the
header to 24 layers (block_count 24, nextn_predict_layers 0, last recurrent_layers entry dropped), re-pads to the
file's alignment, and copies the tensor data byte for byte. Murmur never uses the MTP layer.
"""
import sys

sys.path.insert(0, sys.argv[1])
import gguf  # noqa: E402

src, dst = sys.argv[2], sys.argv[3]
reader = gguf.GGUFReader(src)
align = reader.alignment


def at(key, part):
    field = reader.fields[key]
    return field.offset + sum(p.nbytes for p in field.parts[:part])


last = reader.tensors[-1].field
info_end = last.offset + sum(p.nbytes for p in last.parts)
assert reader.data_offset == -(-info_end // align) * align

with open(src, "rb") as f:
    header = bytearray(f.read(info_end))
    for key, old, new in (("qwen35.block_count", 25, 24), ("qwen35.nextn_predict_layers", 1, 0)):
        field = reader.fields[key]
        assert int(field.parts[field.data[0]][0]) == old
        o = at(key, field.data[0])
        header[o:o + 4] = new.to_bytes(4, "little")
    rec = reader.fields["qwen35.attention.recurrent_layers"]
    assert len(rec.data) == 25 and int(rec.parts[rec.data[-1]][0]) == 0  # the MTP layer is not recurrent
    o = at(rec.name, 4)  # element count
    header[o:o + 8] = (24).to_bytes(8, "little")
    del header[at(rec.name, rec.data[-1])]
    header += b"\0" * (-len(header) % align)
    with open(dst, "wb") as out:
        out.write(header)
        f.seek(reader.data_offset)
        while chunk := f.read(1 << 24):
            out.write(chunk)

check = gguf.GGUFReader(dst)
assert int(check.fields["qwen35.block_count"].contents()) == 24
assert len(check.fields["qwen35.attention.recurrent_layers"].data) == 24
assert [t.name for t in check.tensors] == [t.name for t in reader.tensors]
for a, b in zip(reader.tensors, check.tensors):
    assert a.data.tobytes() == b.data.tobytes(), a.name
print("patched", dst)
