#!/bin/sh
# Gemma 3 4B (AMD's GenAI build, embedding patched for text-only calls). DirectML is correct only up to
# about 1,000 prompt tokens (its attention ignores the sliding window), so it gets alt text only.
B=bin/bench3/LlmSpike.exe
G=models/gemma3-4b-amd
D=models/gemma3-4b-amd-dml
$B bench --model $G --ep cpu --ctx 16384 --tag g-cpuall-4B --jobs material/jobs-alttext.json
$B bench --model $G --ep cpu --ctx 16384 --chunk 512 --tag g-cpuall-4B --jobs material/jobs-summary.json
$B bench --model $G --ep cpu --ctx 8192 --chunk 512 --tag g-cpuall-4B --prefix material/prompts/toolsearch-prefix.txt --queries material/toolsearch/queries.json
$B bench --model $D --ep dml --vendor 0x1002 --ctx 4096 --tag g-dml-amd-4B --jobs material/jobs-alttext.json
$B bench --model $D --ep dml --vendor 0x10DE --ctx 4096 --tag g-dml-nv-4B --jobs material/jobs-alttext.json
$B bench --model $D --ep dml --vendor 0x1002 --vision-ep cpu --ctx 4096 --tag g-dml-amd-4B-viscpu --jobs material/jobs-alttext.json
$B bench --model $G --ep cpu --threads 4 --ctx 16384 --tag g-cpu4-4B --jobs material/jobs-alttext.json
echo GEMMA-DONE
