#!/bin/sh
# CPU matrix for §11.1. Quality does not depend on the EP, so the larger models' CPU runs also
# answer "is the bigger tier better", while speed for the GPU tiers needs GPU builds.
B=bin/bench/LlmSpike.exe
M=models
run() { # tag model threads
  for j in alttext summary; do
    $B bench --model $M/$2/onnxruntime/cpu_and_mobile/cpu-int4-rtn-block-32 --ep cpu $3 --ctx 16384 --tag "$1" --jobs material/jobs-$j.json
  done
  $B bench --model $M/$2/onnxruntime/cpu_and_mobile/cpu-int4-rtn-block-32 --ep cpu $3 --ctx 8192 --tag "$1" \
     --prefix material/prompts/toolsearch-prefix.txt --queries material/toolsearch/queries.json
}
run cpu4-2B qwen3-vl-2B "--threads 4"
run cpuall-2B qwen3-vl-2B ""
run cpu4-4B qwen3-vl-4B "--threads 4"
run cpuall-4B qwen3-vl-4B ""
run cpuall-8B qwen3-vl-8B ""
