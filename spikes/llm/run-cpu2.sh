#!/bin/sh
# Second CPU pass: chunked prefill (512) for text, after the first pass ran out of memory.
B=bin/bench/LlmSpike.exe
M=models
CPU=onnxruntime/cpu_and_mobile/cpu-int4-rtn-block-32
sum() { $B bench --model $M/$2/$CPU --ep cpu $3 --ctx 16384 --chunk 512 --tag "$1" --jobs material/jobs-summary.json; }
alt() { $B bench --model $M/$2/$CPU --ep cpu $3 --ctx 16384 --tag "$1" --jobs material/jobs-alttext.json; }
tools() { $B bench --model $M/$2/$CPU --ep cpu $3 --ctx 8192 --chunk 512 --tag "$1" \
     --prefix material/prompts/toolsearch-prefix.txt --queries material/toolsearch/queries.json; }
alt cpu4-2B-redo qwen3-vl-2B "--threads 4"
sum c-cpu4-2B qwen3-vl-2B "--threads 4"
sum c-cpuall-2B qwen3-vl-2B ""
sum c-cpu4-4B qwen3-vl-4B "--threads 4"
sum c-cpuall-4B qwen3-vl-4B ""
tools c-cpuall-4B qwen3-vl-4B ""
alt cpuall-8B qwen3-vl-8B ""
sum c-cpuall-8B qwen3-vl-8B ""
tools c-cpuall-8B qwen3-vl-8B ""
echo MATRIX-DONE
