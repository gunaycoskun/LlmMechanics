# LlmMechanics
This project is about LLM mechanics and measurement evaluations. AI assistance was used.

# Phase 1 — LLM Mechanics: Local Inference Measurements

This directory contains six experiments that measure not **what** an LLM says but
**how it behaves**. The goal is not a demo but to establish the numerical ground
for the architectural decisions taken in the following phases.

The measurements were first made against the Google Gemini API, then moved to
local inference. The reason for the move was not cost but **privacy**: public
procurement and progress payment documents cannot be sent to an external API.
That decision is in [ADR-0004](../../docs/adr/0004-local-inference-lm-studio.md).

---

## Environment

| | |
|---|---|
| CPU | AMD Ryzen 7 9800X3D (8 cores / 16 logical) |
| RAM | 30.9 GB |
| GPU | NVIDIA GeForce RTX 5070 Ti, 15.92 GB VRAM, CUDA |
| Server | LM Studio 0.4.23, llama.cpp CUDA 12 backend |
| Model | `qwen/qwen3.5-9b`, GGUF, Q4_K_M, 6.55 GB |
| Context | 8192 tokens |
| GPU offload | 32/32 layers (full) |
| Max Concurrent Predictions | 16 |
| Date | 2026-09-09 |

Measurement tool: a dependency-free single-file C# console application
(`LlmMechanics/StepOne.cs`, started through `Program.cs`). A raw `HttpClient` is
used; an SDK/abstraction was deliberately avoided, because an abstraction
normalizes exactly the provider differences we want to measure.

### Methodology

- A 3-call **warm-up** runs at the start of every run and is excluded from the
  measurements (model loading, CUDA context init and kernel compilation produce
  outliers on the first calls). The warm-up runs **before** the pre-flight check:
  the other way around, the `ttftAny` printed by the pre-flight check reflected
  the cold cost (2413 ms versus 65 ms) and a wrong baseline was read.
- In the latency experiments a unique GUID is prepended to every prompt. This
  **breaks the KV cache hit on purpose**; without it, prefill is skipped from the
  second call onward and a fake speed-up is measured. The cache effect is
  measured separately and deliberately in Experiment 4.
- The **median** is reported, not the mean: on a GPU, desktop composition and
  thermal fluctuation produce occasional spikes that distort the mean.
- Values that could not be measured are recorded as `-1`, not folded down to `0`.
  Writing `0` reads as "it arrived instantly" and silently makes the CSV lie.

### Known limitation: reasoning could not be disabled

`qwen3.5-9b` is a hybrid reasoning model. It emits thinking tokens in a separate
`reasoning_content` field rather than inside `delta.content`.

Three ways of turning thinking off were tried and **all three were ineffective**:
`chat_template_kwargs.enable_thinking=false`, the `/no_think` system message, and
LM Studio's `Reasoning Budget = 0` setting (with the model reloaded). The
measurements were run with thinking enabled.

This does **not invalidate** the latency coefficients: to the GPU it makes no
difference whether a token is thinking or answer, the decode cost is the same.
Experiment 2 confirms this — tok/s stays in the 118.7–120.6 band across five
different output lengths.

**Where it does matter is Experiment 6:** the thinking on/off comparison could
not be made.

---

## Finding 1 — Turkish token penalty: 39% net, 81% worst case

Semantically equivalent Turkish/English text pairs, a single call with
`max_tokens=1`.

The chat template adds a fixed cost to every prompt. Because that constant
inflates **both the numerator and the denominator**, it artificially pushes the
raw ratio toward 1. We measured the constant by sending a single-character
prompt: **10 tokens** (`.` → 11 prompt_tokens). The net ratio is the real
in-text penalty after subtracting that constant.

| text type | TR | EN | raw ratio | net ratio |
|---|---:|---:|---:|---:|
| short sentence | 31 | 24 | 1.292 | 1.500 |
| technical paragraph | 72 | 60 | 1.200 | 1.240 |
| **document list** | **39** | **26** | **1.500** | **1.812** |
| numeric | 56 | 50 | 1.120 | 1.150 |
| long clause | 92 | 76 | 1.211 | 1.242 |

```
Raw ratio : mean 1.264 | worst 1.500 | token-weighted 1.229
Net ratio : mean 1.389 | worst 1.812
```

The Gemini reference was 1.33 — but that too was measured as a raw ratio, so the
comparison is not apples to apples. Two models cannot be compared without
applying the same correction.

**Decision:** chunk sizing will be computed from the **net worst case (1.81)**.
The highest penalty occurs in short, term-dense lists, and progress payment annex
lists are in exactly that format. Planning with the raw mean of 1.26 means a 44%
overflow risk on real documents.

---

## Finding 2 — 1 output token ≈ 44 input tokens

A two-arm measurement: in arm A the input varies and the output ceiling is
fixed, in arm B the reverse. 5 repetitions per point.

**Arm A — prefill** (temperature=0, max_tokens=64)

| input tokens | ttftAny (ms) | total p50 (ms) |
|---:|---:|---:|
| 55 | 71.4 | 609.2 |
| 95 | 80.3 | 608.3 |
| 261 | 101.7 | 634.8 |
| 874 | 228.9 | 776.3 |
| 2510 | 541.8 | 1104.3 |

**Arm B — decode** (temperature=0.7)

| output tokens | total p50 (ms) | tok/s |
|---:|---:|---:|
| 32 | 337.2 | 118.7 |
| 64 | 600.8 | 120.6 |
| 128 | 1141.7 | 119.8 |
| 256 | 2211.0 | 119.6 |
| 512 | 4365.5 | 119.1 |

Least-squares fit:

```
total ≈ 54 ms + 0.1925 × input_tokens + 8.395 × output_tokens
                  R²=1.000               R²=1.000  (~119.1 tok/s)
```

**1 output token costs as much as 44 input tokens.**

### Why the prefill coefficient is measured from ttft

The two methods give two different numbers:

```
a_ttft  = 0.1925 ms/token   (R²=1.000)  ← the one used
a_total = 0.2050 ms/token   (R²=0.999)
```

The reason: in arm A `max_tokens` is a **ceiling**, not a fixed value. If the
output length moves together with the input, the decode term leaks into the
total-based slope — the classic omitted-variable bias. `ttftAny` stamps the
moment prefill finishes and is completely independent of decode; it is the right
instrument for the prefill coefficient.

In this run the output length stayed at exactly 64 at every point in arm A
(`out± = 0`), so the difference is small. Had it not stayed constant, `a_total`
would have been noticeably inflated.

### The physical cause

Prefill processes input tokens **in parallel** (compute-bound). Decode produces
output tokens **sequentially**, reading all model weights from memory at every
step (memory-bandwidth-bound). tok/s staying between 118.7 and 120.6 across five
measurements in arm B shows the measurement really did hit the memory bandwidth
limit.

**Decisions:**
- Shortening the prompt is a low-return optimization. A 2510-token prompt is only
  483 ms of prefill.
- `top_k` can be generous in RAG. Sending 10 chunks instead of 3 costs ~100 ms of
  latency; the retrieval quality gain more than covers it.
- "Answer briefly and concisely" is not a stylistic preference but the **primary
  latency lever**.
- Reasoning models are expensive in this economy: thinking tokens are counted on
  the output side, i.e. the side that is 44 times more expensive.

---

## Finding 3 — Prompt ordering creates a 91% latency difference

There are two separate truths here and they must not be conflated:

- **Protocol level:** every turn resends the whole history, so token cost
  accumulates. That does not change.
- **Server level:** for a request continuing with the same prefix, prefill is not
  recomputed, it comes from the KV cache.

A ~4200-token document was placed in the system message and 5 turns of questions
were asked. The only difference: in the `cache_hostile` arm a 32-character GUID
was prepended to the **front** of the system message on every turn.

| turn | cache_friendly ttftAny | cache_hostile ttftAny |
|---:|---:|---:|
| 1 | 851 ms | 885 ms |
| 2 | **85 ms** | 893 ms |
| 3 | **82 ms** | 909 ms |
| 4 | **82 ms** | 924 ms |
| 5 | **85 ms** | 907 ms |

Median excluding turn 1: **83 ms vs 908 ms — an 825 ms difference, a 91% saving.**
The same holds for total time: 1345 ms vs 2174 ms.

### The two experiments confirm each other

This result can be predicted in advance from Experiment 2's prefill coefficient:

```
Experiment 4 (measured)   : 825 ms / 4244 tokens = 0.1944 ms/token
Experiment 2 (independent): a_ttft               = 0.1925 ms/token
difference: 1%
```

Two independent experiments, two different methods, the same physical constant.
That is the evidence that the measurement methodology was set up correctly.

### A cache hit is not free

```
cache_friendly : 0.0198 ms/token
cache_hostile  : 0.2140 ms/token
```

A cache hit does not zero out prefill, it makes it ~10 times cheaper. The
remaining cost is the cache lookup itself. The assumption "if it hits the cache
there is no cost" is wrong.

**Decision:** in a prompt, **fixed content first, variable content last.**
System prompt + contract text first, the user's question afterwards. Putting
fields that change on every request — timestamp, request-id, session information
— at the front of the prompt breaks the prefix match and burns ~825 ms every
turn. This is not a style preference but a measured performance rule.

### The limit of this experiment

In **all ten** of the turns the assistant answer came back empty: the entire
150-token budget went to reasoning and `(no answer)` was appended to the history.

The KV cache comparison is **valid** — both arms ran under the same conditions
with the GUID as the only variable. But this is not a real five-turn conversation,
it is a five-turn prompt-growth simulation. In a real conversation the assistant
answers would also be appended to the history and would inflate the prompt
faster. **The quadratic token accumulation claim is not supported by this data**;
only cache behaviour is being shown.

---

## Finding 4 — Determinism comes from sampling, not from the seed

The same prompt 20 times, across four arms (max_tokens=2500).

| arm | unique | excl. empty | empty | avg. length |
|---|---:|---:|---:|---:|
| temp=0.0, seed=42 | 1/20 | 1/20 | 0 | 113 chars |
| temp=0.0, no seed | 1/20 | 1/20 | 0 | 113 chars |
| temp=0.8, no seed | 19/20 | 18/18 | 2 | 161 chars |
| temp=1.8, no seed | 20/20 | 20/20 | 0 | 242 chars |

The real finding: at `temp=0` the output is deterministic **even without a seed**.
What provides determinism is not the seed but greedy sampling (the
highest-probability token at every step). A seed only matters when `temp>0`.

The "10/10 different outputs" observed on Gemini was not the nature of the model
but a sampling setting. Model weights are a fixed function.

`temp=1.8` is a **control arm**: it verifies that the temperature parameter really
reaches the model. If it did not, we would see uniform output there too.

### The limits of this experiment

- **The variety metric is weak.** "Unique" is exact string equality and saturates
  at n. It cannot draw a meaningful distinction between temp=0.8 (19/20) and
  temp=1.8 (20/20). That is why the control arm is a weak signal. A graded metric
  (Levenshtein, self-BLEU) would be needed; the raw outputs are written to disk
  for exactly that calculation.
- **The temp=0.8 arm is dirty:** 2 empty outputs (the budget went to reasoning)
  and 2 truncated outputs (the max_tokens ceiling). The variety count is
  comparing truncated texts.
- **Batching was not measured.** The calls are sequential, only one request is in
  flight at a time. Server-side batching may change the floating-point summation
  order on the GPU and break determinism even at `temp=0`. That cannot be measured
  with this design. **It must be measured again when agent replay is designed in
  Phase 5.**

---

## Finding 5 — Saturation at a concurrency of 16

There is no rate limit locally; the limit is the GPU itself.

| parallel | total tok/s | tok/s per request | median (ms) | slowest (ms) |
|---:|---:|---:|---:|---:|
| 1 | 113.1 | 113.1 | 1767 | 1767 |
| 2 | 113.6 | 56.8 | 2665 | 3520 |
| 4 | 175.3 | 43.8 | 3561 | 4563 |
| 8 | 265.3 | 33.2 | 6024 | 6030 |
| **16** | **443.3** | **27.7** | 6494 | 7218 |
| 32 | 458.5 | 14.3 | 10512 | 13956 |

Going from 16 to 32, total throughput rises by only 3% while the slowest request
goes from 7218 to 13956 ms. **The saturation point is 16.**

Two details:

- **There is no gain from 1 to 2** (113.1 → 113.6). Batching does not engage yet
  at two requests, they simply queue. The real gain starts after 4.
- **Capacity planning comes from the per-request figure, not from total
  throughput.** At 16 concurrent users each one runs at a quarter of the speed of
  a single user.

### The limit you measure is the tightest limit

In the first run the curve flattened after 8 and this was taken for GPU
saturation. The real cause was LM Studio's default of
`Max Concurrent Predictions: 4` — that is, a single configuration line. When the
setting was raised to 16, throughput went from 208 to 445 tok/s.

When measuring capacity, **first verify that artificial ceilings have been
removed**, or you will base a hardware decision on a config line. The highest
concurrency level tested must be below the server's concurrency setting;
otherwise the "saturation point" is a circular result.

### The limits of this experiment

- **The "slowest" column is not a tail statistic.** While n ≤ 20, nearest-rank
  p95 is mathematically equal to the maximum. It shows the slowest request of a
  single batch, not the tail of the distribution.
- **No repetition, no counterbalancing.** The levels were measured in a single
  run and always in increasing order; the highest concurrency was always on the
  hottest GPU. The thermal effect cannot be separated out.

---

## Finding 6 — The invisible cost of reasoning

Eight regulation questions are dumped to `06_quality_spot_check.md` to be assessed
by hand. An automatic score is **deliberately not produced** — a real eval harness
is the job of Phases 3-4.

The model was not trained on the regulations; errors are the expected result and
are the evidence for why RAG is needed.

**The planned comparison could not be made.** Two arms, thinking on and off, were
designed; because thinking could not be disabled, the two arms sent byte-identical
requests (in the first run the token counts were identical on 8/8 questions). The
harness now detects this situation and runs a single arm.

The one concrete measurement that remains is enough to show the scale. For the
question "What is a progress payment? One sentence.":

```
reasoning : 1016 tokens
content   :   25 tokens
ratio     : 40:1
```

Translated through Experiment 2's coefficient:

```
1016 × 8.395 ms = 8.5 seconds  (which the user does not see)
  25 × 8.395 ms = 0.2 seconds  (which the user does see)
```

The user stares at a blank screen for 8.5 seconds. This is why `ttftAny` and
`ttftContent` must be measured separately: if the timeout budget is not built on
`ttftContent`, the system cancels healthy requests.

On top of that, the model that thought for 1016 tokens **gave the wrong answer** —
it defined a progress payment as an amount "paid after the work is completed and
accepted"; a progress payment is paid at interim stages. **Reasoning does not
close a gap in domain knowledge.**

---

## The measurement itself can corrupt the measurement

Three silent bugs were found in this phase. What all three have in common: the
program did not crash, the numbers looked reasonable, they were simply wrong.

**1. The unread channel.** The first version read the model's `content` field;
this model emits thinking in the `reasoning_content` field. The result: all
outputs appeared empty and the TTFT stamp landed at the end of generation, i.e.
`ttft == total`. Experiment 3's "1/20 unique" result was in fact the equality of
20 empty strings.

**2. The fallback left on.** The `/no_think` system message tried as a way of
disabling thinking did not work, but was left enabled and added a fixed turn to
every subsequent call. Experiment 1's TR/EN ratio fell from 1.264 to 1.214 —
because a constant term inflates both the numerator and the denominator. The
fallback is now rolled back automatically when it does not work.

**3. Recording the unmeasurable as if it had been measured.** When `ttftAny` could
not be found it was folded down to `totalMs`. That reproduced the pathology
created by the first bug, unflagged, and entered the regression as if it were a
real measurement. It now stays as `-1` and is excluded from the calculations.

**The countermeasure taken:** every run executes a **pre-flight check** step before
measuring — which channel reasoning arrives on, whether thinking can be disabled,
whether the server sends `usage`. The coefficients are also reported together with
R², and suspect values (negative overhead, low R², more than 50% divergence
between methods) are printed explicitly as warnings.

The measurement tool is itself part of the system being measured.

---

## Decisions carried into the following phases

| Decision | Basis |
|---|---|
| Chunk size will be computed with the net worst-case multiplier **1.81** | Finding 1 |
| RAG `top_k` generous; shortening the prompt is low priority | Finding 2 |
| Limiting answer length is the primary latency lever | Finding 2 |
| Prompt layout: fixed content first, variable content last | Finding 3 |
| A cache hit is not free (~10x cheaper), it will not be counted as zero in planning | Finding 3 |
| `temperature=0` where deterministic output is required; the seed is secondary | Finding 4 |
| The local capacity ceiling is 16 concurrent requests | Finding 5 |
| The timeout budget will be built on `ttftContent` | Finding 6 |
| A reasoning model will not be used in low-latency scenarios | Finding 6 |

## Open items

- Determinism under concurrent batching was not measured (Phase 5).
- The reasoning on/off quality-cost comparison could not be made.
- No comparison run against a non-reasoning instruct model was made.
- Quality assessment will be done by hand; automatic eval is Phases 3-4.
- The embedding model (`text-embedding-qwen3-embedding-0.6b`) was loaded but not
  measured in this phase.
- Experiment 5 has no repetition or counterbalancing; the thermal effect was not
  separated out.

---

## Running it

The first argument selects the phase (`1` for this one); everything after it is
passed to the phase unchanged.

```bash
cd LlmMechanics
dotnet run -- 1 --model qwen/qwen3.5-9b            # all experiments
dotnet run -- 1 --model qwen/qwen3.5-9b 2 4        # selected experiments
dotnet run -- 1 --model qwen/qwen3.5-9b --doc-words 1000
```

Outputs are written under `results/`. Every row is stamped with a `run_id` and, if
the schema matches, appended to the CSV rather than overwriting it — so runs are
comparable. If the schema changes, the old file is kept as `.bak`. Files filled in
by hand (`03_sample_*.txt`, `06_quality_spot_check.md`) are never overwritten; if
one exists, the output is written to a sibling file with the `run_id` appended.
