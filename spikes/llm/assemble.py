"""Assemble a GenAI model directory: a builder-made decoder plus the published vision and
embedding graphs, which the builder does not make for Qwen3-VL."""
import json, os, shutil, sys

built, published, out = sys.argv[1:4]
os.makedirs(out, exist_ok=True)
for f in os.listdir(published):
    if f.startswith(("vision", "embedding")) or f in ("processor_config.json",):
        dst = os.path.join(out, f)
        if not os.path.exists(dst):
            os.link(os.path.join(published, f), dst)
for f in os.listdir(built):
    if f != "genai_config.json":
        dst = os.path.join(out, f)
        if not os.path.exists(dst):
            os.link(os.path.join(built, f), dst)
# The builder's GPU decoders take fp16 embeddings; the published embedding graph emits fp32, and
# GenAI passes the buffer across without converting. Add a Cast at the embedding graph's output.
import onnx
from onnx import helper, TensorProto
dec_in = {i.name: i.type.tensor_type.elem_type for i in onnx.load(os.path.join(out, "model.onnx"), load_external_data=False).graph.input}
emb_path = os.path.join(out, "embedding.onnx")
emb = onnx.load(emb_path, load_external_data=False)
out_vi = emb.graph.output[0]
want = dec_in["inputs_embeds"]
if out_vi.type.tensor_type.elem_type != want:
    for n in emb.graph.node:
        n.output[:] = [o + "_f32" if o == out_vi.name else o for o in n.output]
    emb.graph.node.append(helper.make_node("Cast", [out_vi.name + "_f32"], [out_vi.name], to=want, name="humcp_cast_embeds"))
    out_vi.type.tensor_type.elem_type = want
    os.remove(emb_path)  # a hard link to the published graph; write a new file, keep the shared .data
    onnx.save(emb, emb_path)
    print("embedding output cast to", TensorProto.DataType.Name(want))
cfg = json.load(open(os.path.join(published, "genai_config.json")))
dec = json.load(open(os.path.join(built, "genai_config.json")))["model"]["decoder"]
cfg["model"]["decoder"] = dec
json.dump(cfg, open(os.path.join(out, "genai_config.json"), "w"), indent=4)
print("assembled", out, "decoder", dec["filename"], dec["session_options"]["provider_options"])
