# vLLM Dev Cheatsheet (RTX 3080 16GB)

## Local model status

Detected local checkpoint:
- `D:\Models\Qwen3.5-9B-AWQ-4bit`

The folder already contains a Hugging Face safetensors checkpoint (`config.json`, tokenizer files, and `model.safetensors-*` shards), so no conversion is needed.

For a 16GB GPU, the dev Dockerfile now serves the 4-bit checkpoint with this launch profile:
- model path: `/models/Qwen3.5-9B-AWQ-4bit`
- served model name: `Qwen3.5-9B-AWQ-4bit-local`
- dtype: `half`
- quantization: `compressed-tensors`
- context: `5120`
- KV cache dtype: `fp8`
- prefix caching: enabled
- max concurrent sequences: `1`

## Build and run

```powershell
docker build -f LEPTA.vLLM/dev/dockerfile.vLLM-Dev -t lepta-vllm-dev:latest .
docker run --rm --gpus all -p 8512:8512 --mount type=bind,source=D:/Models/Qwen3.5-9B-AWQ-4bit,target=/models/Qwen3.5-9B-AWQ-4bit,readonly --name lepta-vllm-dev lepta-vllm-dev:latest
```

On this Windows + PowerShell setup, `-v "D:\Models\...:/models/...:ro"` failed with `invalid volume specification`, while the `--mount` form above started the container correctly.

## Quick server checks

```powershell
curl.exe -s -o NUL -w "%{http_code}" http://localhost:8512/health
Invoke-RestMethod http://localhost:8512/v1/models
docker logs --tail 100 lepta-vllm-dev
```

`/health` returns an empty body when healthy, so `200` from `curl.exe` is the expected success signal.

`/v1/models` should report `Qwen3.5-9B-AWQ-4bit-local`. If it reports `Qwen3.5-9B-local`, you are still talking to an older container or an older mount path.

## Run the explicit unit test against the server

```powershell
dotnet test .\LEPTA.Tests\LEPTA.Tests.csproj -c Release --no-build --filter "Category=Integration"
```

## If VRAM pressure is high

- Lower `--max-model-len` from `5120` to `4096` or `2048` in `LEPTA.vLLM/dev/dockerfile.vLLM-Dev`.
- Keep `--max-num-seqs 1` until startup is stable.
- Reduce `--gpu-memory-utilization` below `0.90` if the container becomes unstable during startup.

