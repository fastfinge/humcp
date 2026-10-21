"""Run GenAI's model builder, giving every provider the decomposed M-RoPE that it already uses for
TensorRT-RTX: Windows ML's ONNX Runtime (1.27) has no com.microsoft:MRotaryEmbedding."""
import os, runpy, sys
import onnxruntime_genai.models as m

# builder.py imports its siblings as top-level modules (`from builders import ...`), so the copy
# to patch is that one, not onnxruntime_genai.models.builders.
sys.path.insert(0, os.path.dirname(m.__file__))
from builders import base
from builders.expansions import TRT_RTX

for n in ("make_mrotary_embedding", "make_expansion_constant", "get_mrope_owners",
          "make_mrope_positions", "make_mrope_cache", "make_mrope_rotation", "make_mrope_output"):
    setattr(base.Model, n, getattr(TRT_RTX, n))
sys.argv = ["builder"] + sys.argv[1:]
runpy.run_module("onnxruntime_genai.models.builder", run_name="__main__")
