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
- context: `8192`
- KV cache dtype: `fp8`
- prefix caching: enabled
- max concurrent sequences: `5`
- CPU offload: `0 GB`

The locally built dev image currently reports `vLLM 0.20.2`. In that version, `swap_space` is deprecated and ignored, so the dev launcher does not wire `--swap-space` even though older repo examples and tests still mention it.

## Common tuning env vars

You can override these at runtime in PowerShell before `docker compose up`:

```powershell
$env:VLLM_CPU_OFFLOAD_GB = "0"
$env:TOKENIZERS_PARALLELISM = "true"
```

## CPU offload and “how many layers”

`--cpu-offload-gb` does **not** let you choose an exact number of transformer layers to place on CPU.

In vLLM, this setting is a **host RAM budget**. The runtime decides what to keep resident on GPU versus what to page through CPU memory based on that memory allowance and the model's internal layout.

So the tuning model is:

- `VLLM_CPU_OFFLOAD_GB=0` → keep everything possible on GPU
- `VLLM_CPU_OFFLOAD_GB=4` or `8` → allow that much CPU RAM to help the model fit
- larger values → may help fit tighter VRAM setups, but usually increase latency and reduce throughput

If you want to think in “layers”, vLLM does not expose a stable `--gpu-layers` style control here. That kind of layer-count knob is more common in `llama.cpp` / GGUF workflows than in vLLM.

Practical approach:

1. Start with `VLLM_CPU_OFFLOAD_GB=0`.
2. If the model does not fit or startup is unstable, try `4`.
3. If needed, try `8` or `12`.
4. Lower `VLLM_MAX_MODEL_LEN` before pushing CPU offload too high if throughput matters.

## Build and run

```powershell
docker compose -f .\LEPTA.vLLM\dev\docker-compose.vllm-dev.yml up --build
```

Use `Ctrl+C` to stop the foreground process, or run detached:

```powershell
docker compose -f .\LEPTA.vLLM\dev\docker-compose.vllm-dev.yml up --build -d
```

Bring it down with:

```powershell
docker compose -f .\LEPTA.vLLM\dev\docker-compose.vllm-dev.yml down
```

`--rm` is useful with `docker run` when you want Docker to delete the stopped container automatically. With `docker compose up`, you normally do **not** use `--rm`; Compose keeps the container so you can inspect logs, restart it quickly, and remove it explicitly with `docker compose down`.

If you still want the equivalent one-off `docker run` command, this remains valid:

```powershell
docker build -f LEPTA.vLLM/dev/dockerfile.vLLM-Dev -t lepta-vllm-dev:latest .
docker run --rm --gpus all -p 8512:8512 --mount type=bind,source=D:/Models/Qwen3.5-9B-AWQ-4bit,target=/models/Qwen3.5-9B-AWQ-4bit,readonly --name lepta-vllm-dev lepta-vllm-dev:latest
```

On this Windows + PowerShell setup, `-v "D:\Models\...:/models/...:ro"` failed with `invalid volume specification`, while the `--mount` form above started the container correctly.

## Quick server checks

```powershell
curl.exe -s -o NUL -w "%{http_code}" http://localhost:8512/health
Invoke-RestMethod http://localhost:8512/v1/models
docker compose -f .\LEPTA.vLLM\dev\docker-compose.vllm-dev.yml logs --tail 100
```

`/health` returns an empty body when healthy, so `200` from `curl.exe` is the expected success signal.

`/v1/models` should report `Qwen3.5-9B-AWQ-4bit-local`. If it reports `Qwen3.5-9B-local`, you are still talking to an older container or an older mount path.

## Run the explicit unit test against the server

```powershell
dotnet test .\LEPTA.Tests\LEPTA.Tests.csproj -c Release --no-build --filter "Category=Integration"
```

## If VRAM pressure is high

- Lower `--max-model-len` from `8192` to `4096` or `2048` in `LEPTA.vLLM/dev/dockerfile.vLLM-Dev`.
- Keep `--max-num-seqs` conservative until startup is stable.
- Reduce `--gpu-memory-utilization` below `0.90` if the container becomes unstable during startup.
- Keep `VLLM_CPU_OFFLOAD_GB=0` unless you need RAM-assisted fit more than raw throughput.

