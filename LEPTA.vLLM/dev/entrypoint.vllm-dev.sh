#!/bin/sh
set -eu

exec python3 -m vllm.entrypoints.openai.api_server \
  --model /models/Qwen3.5-9B-AWQ-4bit \
  --served-model-name Qwen3.5-9B-AWQ-4bit-local \
  --host 0.0.0.0 \
  --port "${VLLM_PORT}" \
  --dtype "${VLLM_DTYPE}" \
  --quantization compressed-tensors \
  --gpu-memory-utilization "${VLLM_GPU_MEMORY_UTILIZATION}" \
  --max-model-len "${VLLM_MAX_MODEL_LEN}" \
  --kv-cache-dtype "${VLLM_KV_CACHE_DTYPE}" \
  --cpu-offload-gb "${VLLM_CPU_OFFLOAD_GB}" \
  --enable-prefix-caching \
  --max-num-seqs "${VLLM_MAX_NUM_SEQS}" \
  --tensor-parallel-size 1 \
  --reasoning-parser qwen3 \
  "$@"

