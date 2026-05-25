#!/usr/bin/env python3
import argparse
import concurrent.futures
import json
import statistics
import time
import uuid
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Iterable, Optional

import requests


REQUEST_TIMEOUT = 900


@dataclass
class StreamPiece:
    at_s: float
    text: str


@dataclass
class RequestTrace:
    request_name: str
    prompt_label: str
    request_start_s: float
    first_token_s: Optional[float]
    completed_s: float
    prompt_tokens: int
    completion_tokens: int
    finish_reason: Optional[str]
    output_text: str
    stream_pieces: list[StreamPiece] = field(default_factory=list)


def now_s() -> float:
    return time.perf_counter()


def wait_for_server(base_url: str, timeout_s: float) -> None:
    deadline = time.time() + timeout_s
    while time.time() < deadline:
        try:
            response = requests.get(f"{base_url}/health", timeout=5)
            if response.status_code == 200:
                return
        except requests.RequestException:
            pass
        time.sleep(1.0)
    raise TimeoutError(f"Timed out waiting for {base_url} to become healthy.")


def discover_model(base_url: str) -> str:
    response = requests.get(f"{base_url}/v1/models", timeout=30)
    response.raise_for_status()
    payload = response.json()
    models = payload.get("data") or []
    if not models:
        raise RuntimeError("The server did not return any models from /v1/models.")
    return models[0]["id"]


def tokenize_prompt(base_url: str, model: str, prompt: str) -> int:
    response = requests.post(
        f"{base_url}/tokenize",
        json={"model": model, "prompt": prompt},
        timeout=60,
    )
    response.raise_for_status()
    payload = response.json()
    tokens = payload.get("tokens")
    if isinstance(tokens, list):
        return len(tokens)
    count = payload.get("count")
    if isinstance(count, int):
        return count
    raise RuntimeError(f"Unexpected /tokenize payload: {payload}")


def make_doc_paragraph(index: int) -> str:
    return (
        f"Section {index:04d}. The operational record tracks document intake, schema validation, "
        f"priority routing, queue backpressure, retry budgets, storage tiers, audit markers, "
        f"and rollback checkpoints. Service code R{index % 97:02d} coordinates policy set "
        f"P{(index * 7) % 113:03d}, retention class C{(index * 11) % 59:02d}, and reviewer "
        f"group G{(index * 13) % 41:02d}. Notes describe customer-visible symptoms, likely causes, "
        f"mitigations, edge conditions, fallbacks, acceptance criteria, and implementation caveats. "
        f"This passage is intentionally dense so the benchmark sees a realistic long-prefix workload.\n"
    )


def build_document(base_url: str, model: str, target_tokens: int) -> tuple[str, int]:
    lower = 16
    upper = 512
    best_text = ""
    best_tokens = 0

    while lower <= upper:
        mid = (lower + upper) // 2
        candidate = "".join(make_doc_paragraph(i) for i in range(1, mid + 1))
        token_count = tokenize_prompt(base_url, model, candidate)
        if not best_text or abs(token_count - target_tokens) < abs(best_tokens - target_tokens):
            best_text = candidate
            best_tokens = token_count
        if token_count < target_tokens:
            lower = mid + 1
        elif token_count > target_tokens:
            upper = mid - 1
        else:
            break

    return best_text.strip(), best_tokens


def extract_delta_text(choice_delta: dict) -> str:
    content = choice_delta.get("content")
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        parts: list[str] = []
        for item in content:
            if isinstance(item, dict) and item.get("type") == "text":
                parts.append(item.get("text", ""))
        return "".join(parts)
    reasoning = choice_delta.get("reasoning")
    if isinstance(reasoning, str):
        return reasoning
    reasoning = choice_delta.get("reasoning_content")
    if isinstance(reasoning, str):
        return reasoning
    return ""


def stream_chat_completion(
    base_url: str,
    model: str,
    request_name: str,
    prompt_label: str,
    messages: list[dict],
    cache_salt: str,
    max_tokens: int,
    temperature: float,
) -> RequestTrace:
    request_start_s = now_s()
    first_token_s: Optional[float] = None
    completed_s = request_start_s
    prompt_tokens = 0
    completion_tokens = 0
    finish_reason: Optional[str] = None
    output_parts: list[str] = []
    stream_pieces: list[StreamPiece] = []

    payload = {
        "model": model,
        "messages": messages,
        "temperature": temperature,
        "max_tokens": max_tokens,
        "stream": True,
        "stream_options": {"include_usage": True},
        "cache_salt": cache_salt,
        "chat_template_kwargs": {"enable_thinking": False},
    }

    with requests.post(
        f"{base_url}/v1/chat/completions",
        json=payload,
        stream=True,
        timeout=REQUEST_TIMEOUT,
    ) as response:
        response.raise_for_status()
        for raw_line in response.iter_lines(decode_unicode=True):
            if not raw_line or not raw_line.startswith("data: "):
                continue
            data = raw_line[6:]
            if data == "[DONE]":
                break
            event = json.loads(data)
            usage = event.get("usage")
            if isinstance(usage, dict):
                prompt_tokens = int(usage.get("prompt_tokens") or prompt_tokens or 0)
                completion_tokens = int(usage.get("completion_tokens") or completion_tokens or 0)
            choices = event.get("choices") or []
            if not choices:
                continue
            choice = choices[0]
            finish_reason = choice.get("finish_reason") or finish_reason
            delta_text = extract_delta_text(choice.get("delta") or {})
            if delta_text:
                completed_s = now_s()
                if first_token_s is None:
                    first_token_s = completed_s
                output_parts.append(delta_text)
                stream_pieces.append(StreamPiece(at_s=completed_s, text=delta_text))

    completed_s = max(completed_s, now_s())
    return RequestTrace(
        request_name=request_name,
        prompt_label=prompt_label,
        request_start_s=request_start_s,
        first_token_s=first_token_s,
        completed_s=completed_s,
        prompt_tokens=prompt_tokens,
        completion_tokens=completion_tokens,
        finish_reason=finish_reason,
        output_text="".join(output_parts),
        stream_pieces=stream_pieces,
    )


def request_messages(document_text: str, task_text: str) -> list[dict]:
    return [
        {
            "role": "system",
            "content": (
                "You answer directly from the supplied document. "
                "Do not include hidden reasoning. Prefer dense factual output."
            ),
        },
        {
            "role": "user",
            "content": (
                "Document follows.\n\n"
                f"{document_text}\n\n"
                "Instruction:\n"
                f"{task_text}"
            ),
        },
    ]


def benchmark_tasks() -> list[str]:
    return [
        "Write exactly 24 short bullet points describing reliability risks, each bullet one sentence.",
        "Write exactly 24 short bullet points describing performance bottlenecks, each bullet one sentence.",
        "Write exactly 24 short bullet points describing data integrity concerns, each bullet one sentence.",
        "Write exactly 24 short bullet points describing recovery procedures, each bullet one sentence.",
        "Write exactly 24 short bullet points describing monitoring signals, each bullet one sentence.",
        "Write exactly 24 short bullet points describing operator mistakes to avoid, each bullet one sentence.",
        "Write exactly 24 short bullet points describing scaling constraints, each bullet one sentence.",
        "Write exactly 24 short bullet points describing security considerations, each bullet one sentence.",
        "Write exactly 24 short bullet points describing user-visible failure modes, each bullet one sentence.",
        "Write exactly 24 short bullet points describing rollout safeguards, each bullet one sentence.",
    ]


def run_parallel_requests(
    base_url: str,
    model: str,
    document_text: str,
    tasks: Iterable[str],
    scenario_name: str,
    cache_salt: str,
    max_tokens: int,
    temperature: float,
) -> list[RequestTrace]:
    traces: list[RequestTrace] = []
    task_list = list(tasks)
    with concurrent.futures.ThreadPoolExecutor(max_workers=len(task_list)) as executor:
        futures = [
            executor.submit(
                stream_chat_completion,
                base_url,
                model,
                f"{scenario_name}-req-{index + 1}",
                task_text,
                request_messages(document_text, task_text),
                cache_salt,
                max_tokens,
                temperature,
            )
            for index, task_text in enumerate(task_list)
        ]
        for future in concurrent.futures.as_completed(futures):
            traces.append(future.result())
    traces.sort(key=lambda item: item.request_name)
    return traces


def partial_tokens_until(base_url: str, model: str, traces: list[RequestTrace], cutoff_s: float) -> int:
    total = 0
    for trace in traces:
        partial_text = "".join(piece.text for piece in trace.stream_pieces if piece.at_s <= cutoff_s)
        if partial_text:
            total += tokenize_prompt(base_url, model, partial_text)
    return total


def scenario_summary(
    scenario_name: str,
    scenario_start_s: float,
    traces_for_total_window: list[RequestTrace],
    traces_for_peak_window: list[RequestTrace],
    base_url: str,
    model: str,
) -> dict:
    scenario_end_s = max(trace.completed_s for trace in traces_for_total_window)
    total_completion_tokens = sum(trace.completion_tokens for trace in traces_for_total_window)
    total_duration_s = scenario_end_s - scenario_start_s
    total_tps = total_completion_tokens / total_duration_s if total_duration_s > 0 else 0.0

    first_token_times = [
        trace.first_token_s for trace in traces_for_peak_window if trace.first_token_s is not None
    ]
    if not first_token_times:
        raise RuntimeError(
            f"{scenario_name} produced no streamed tokens. Inspect the per-request traces for API errors or parser mismatches."
        )
    batch_first_token_s = min(first_token_times)
    batch_first_finish_s = min(trace.completed_s for trace in traces_for_peak_window)
    peak_window_tokens = partial_tokens_until(base_url, model, traces_for_peak_window, batch_first_finish_s)
    peak_window_duration_s = batch_first_finish_s - batch_first_token_s
    peak_window_tps = peak_window_tokens / peak_window_duration_s if peak_window_duration_s > 0 else 0.0

    ttfts = [
        trace.first_token_s - trace.request_start_s
        for trace in traces_for_peak_window
        if trace.first_token_s is not None
    ]

    return {
        "scenario": scenario_name,
        "requests": len(traces_for_peak_window),
        "total_window": {
            "duration_s": round(total_duration_s, 3),
            "completion_tokens": total_completion_tokens,
            "tokens_per_s": round(total_tps, 2),
        },
        "first_response_window": {
            "duration_s": round(peak_window_duration_s, 3),
            "estimated_completion_tokens": peak_window_tokens,
            "tokens_per_s": round(peak_window_tps, 2),
        },
        "ttft_s": {
            "min": round(min(ttfts), 3),
            "median": round(statistics.median(ttfts), 3),
            "max": round(max(ttfts), 3),
        },
        "request_completion_tokens": {
            trace.request_name: trace.completion_tokens for trace in traces_for_peak_window
        },
    }


def print_summary(results: dict) -> None:
    print(json.dumps(results["summaries"], indent=2))
    print()
    for summary in results["summaries"]:
        total = summary["total_window"]
        peak = summary["first_response_window"]
        ttft = summary["ttft_s"]
        print(
            f"{summary['scenario']}: "
            f"total={total['tokens_per_s']} tok/s over {total['duration_s']}s, "
            f"first-window={peak['tokens_per_s']} tok/s over {peak['duration_s']}s, "
            f"TTFT median={ttft['median']}s"
        )


def main() -> None:
    parser = argparse.ArgumentParser(description="Benchmark vLLM prefix caching scenarios.")
    parser.add_argument("--base-url", default="http://127.0.0.1:8512")
    parser.add_argument("--model", default="")
    parser.add_argument("--target-doc-tokens", type=int, default=6000)
    parser.add_argument("--max-tokens", type=int, default=1000)
    parser.add_argument("--temperature", type=float, default=0.0)
    parser.add_argument("--output-json", default="")
    parser.add_argument("--wait-timeout", type=float, default=300.0)
    args = parser.parse_args()

    wait_for_server(args.base_url, args.wait_timeout)
    model = args.model or discover_model(args.base_url)
    document_text, document_tokens = build_document(args.base_url, model, args.target_doc_tokens)
    tasks = benchmark_tasks()
    run_id = uuid.uuid4().hex[:8]

    scenario1_start = now_s()
    scenario1_salt = f"scenario1-{run_id}"
    warmup_trace = stream_chat_completion(
        args.base_url,
        model,
        "scenario1-prefill",
        "Warm the prefix cache with the shared document prefix.",
        request_messages(document_text, "Reply with the single word READY."),
        scenario1_salt,
        8,
        args.temperature,
    )
    scenario1_batch = run_parallel_requests(
        args.base_url,
        model,
        document_text,
        tasks,
        "scenario1",
        scenario1_salt,
        args.max_tokens,
        args.temperature,
    )
    scenario1_summary = scenario_summary(
        "scenario1_prefill_then_10_parallel",
        scenario1_start,
        [warmup_trace, *scenario1_batch],
        scenario1_batch,
        args.base_url,
        model,
    )

    scenario2_start = now_s()
    scenario2_batch = run_parallel_requests(
        args.base_url,
        model,
        document_text,
        tasks,
        "scenario2",
        f"scenario2-{run_id}",
        args.max_tokens,
        args.temperature,
    )
    scenario2_summary = scenario_summary(
        "scenario2_10_parallel_no_prefill",
        scenario2_start,
        scenario2_batch,
        scenario2_batch,
        args.base_url,
        model,
    )

    scenario3_start = now_s()
    scenario3_batch = run_parallel_requests(
        args.base_url,
        model,
        document_text,
        tasks[:1],
        "scenario3",
        f"scenario3-{run_id}",
        args.max_tokens,
        args.temperature,
    )
    scenario3_summary = scenario_summary(
        "scenario3_single_request",
        scenario3_start,
        scenario3_batch,
        scenario3_batch,
        args.base_url,
        model,
    )

    results = {
        "base_url": args.base_url,
        "model": model,
        "document_tokens": document_tokens,
        "run_id": run_id,
        "summaries": [scenario1_summary, scenario2_summary, scenario3_summary],
        "traces": {
            "scenario1": [asdict(warmup_trace)] + [asdict(trace) for trace in scenario1_batch],
            "scenario2": [asdict(trace) for trace in scenario2_batch],
            "scenario3": [asdict(trace) for trace in scenario3_batch],
        },
    }

    if args.output_json:
        Path(args.output_json).write_text(json.dumps(results, indent=2), encoding="utf-8")

    print_summary(results)


if __name__ == "__main__":
    main()
