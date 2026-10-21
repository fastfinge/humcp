#!/bin/sh
# GPU pass: only TensorRT-RTX with unquantized weights produces correct output (see the spike doc).
B=bin/bench2/LlmSpike.exe
M=models/gpu/qwen3-vl-2B-trt-fp16
for j in alttext summary; do $B bench --model $M --ep NvTensorRtRtx --ctx 16384 --tag trt-2B-fp16 --jobs material/jobs-$j.json; done
$B bench --model $M --ep NvTensorRtRtx --ctx 8192 --tag trt-2B-fp16 --prefix material/prompts/toolsearch-prefix.txt --queries material/toolsearch/queries.json
echo GPU-DONE
