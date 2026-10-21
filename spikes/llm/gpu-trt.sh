#!/bin/sh
# Build and assemble an int4/BF16-activation TensorRT-RTX decoder for one model size.
# fp16 activations overflow (every token "!"), and the shared KV buffer does not match the
# graph's present shape, so sharing and CUDA graphs are off.
S=$1   # 2B | 4B | 8B
builder-venv/Scripts/python.exe build-gpu.py -i models/hf/Qwen3-VL-$S-Instruct -o models/build/qwen3-vl-$S-trt-int4bf16 -p int4 -e NvTensorRtRtx \
  -c models/build/cache --extra_options exclude_embeds=true shared_embeddings=false use_cuda_bf16=true $EXTRA > out/build-$S-trt.log 2>&1 || { tail -5 out/build-$S-trt.log; exit 1; }
builder-venv/Scripts/python.exe -I assemble.py models/build/qwen3-vl-$S-trt-int4bf16 models/qwen3-vl-$S/onnxruntime/cpu_and_mobile/cpu-int4-rtn-block-32 models/gpu/qwen3-vl-$S-trt
builder-venv/Scripts/python.exe -I -c "
import json;G='models/gpu/qwen3-vl-$S-trt/genai_config.json';c=json.load(open(G))
c['model']['decoder']['session_options']['provider_options']=[{'NvTensorRtRtx':{}}];c['search']['past_present_share_buffer']=False
json.dump(c,open(G,'w'),indent=2)"
du -sh models/build/qwen3-vl-$S-trt-int4bf16
