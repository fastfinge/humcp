#!/bin/sh
# Phi-4-multimodal (Microsoft's GPU int4 build) on DirectML. --vendor picks the GPU.
B=bin/bench6/LlmSpike.exe
for v in 0x1002 0x10DE; do
  $B bench --model models/phi4mm-dml --ep asis --vendor $v --ctx 16384 --tag p-dml-$v --jobs material/jobs-summary.json
  $B bench --model models/phi4mm-dml --ep asis --vendor $v --ctx 8192 --tag p-dml-$v --jobs material/jobs-toolsearch-full.json
  $B bench --model models/phi4mm-dml --ep asis --vendor $v --ctx 8192 --tag p-dml-$v --jobs material/jobs-alttext.json
done
echo PHI-DONE
