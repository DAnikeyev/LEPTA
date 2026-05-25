# LEPTA — Local Efficient Parallel Text Augmentation

LEPTA is a Windows desktop tool that runs **multiple specialized prompts in parallel** against a local vLLM server, using **shared prefix KV caching** to make parallel inference nearly as fast as a single request.

## What it actually does

You copy text to the clipboard, hit a hotkey, and LEPTA sends N panel prompts to your local model at the same time. Each panel gets the same source document plus its own instruction (summary, risks, action items, code review, etc.). All panels share a single system prompt and source text prefix, so vLLM computes the KV cache once and reuses it across every parallel request. The result is N specialized answers delivered side-by-side in one shot.

## Why it is fast

vLLM stores the key-value state for the shared prefix (system instructions + source document) in memory. When LEPTA fires parallel panel requests, each request reuses that cached prefix instead of re-computing it. On a laptop with an RTX 3080 (16 GB VRAM) running Qwen 3.5 9B AWQ locally, this turns ~50 tok/s sequential generation into ~270 tok/s effective throughput across panels.

## How it works

1. **Capture** — reads clipboard text on hotkey or button press.
2. **Prefix** — builds one shared prompt prefix: system instructions + general instruction + trimmed source document.
3. **Parallelize** — appends each panel's custom instruction and sends all requests concurrently to `/v1/chat/completions`.
4. **Reuse KV cache** — vLLM caches the prefix tokens once; each panel stream starts from that cached state.
5. **Render** — streams tokens back into Markdown or Mermaid panels in real time.

## Main parts

- **Chat** — single-turn smoke test against the selected server.
- **Dashboard** — a workspace with a general instruction and an ordered list of panels.
- **Panel** — one response surface with its own custom instruction and output format (Markdown or Mermaid).
- **Server profile** — vLLM HTTP endpoint configuration. Supports already-deployed servers and Docker-managed deployments.

## Quick start

1. Start a vLLM server on `http://localhost:8512` (or add your own profile).
2. Open LEPTA, select a dashboard or create one.
3. Copy text, press congihurable hotkey (or the run button).
4. Read the parallel panel outputs.

## Stack

- WPF + .NET 10
- vLLM-compatible OpenAI-style HTTP endpoints
- `Markdig` for Markdown rendering
- NLog for logging

## Build

```powershell
dotnet restore LEPTA.sln
dotnet test LEPTA.Tests --filter "Category!=Integration"
dotnet build LEPTA.sln
```

See `docs/ARCHITECTURE.md` and `docs/DEVELOPMENT_PLAN.md` for implementation details.
