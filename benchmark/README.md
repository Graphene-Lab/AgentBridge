# Memory benchmarks

Reproducible benchmarks for the long-term memory of the AIOrchestrator agent core
(the engine behind [AgentBridge](https://github.com/Graphene-Lab/AgentBridge)).

The full methodology, results and the honest comparison with published systems live in
**[docs/MEMORY-BENCHMARK.md](../docs/MEMORY-BENCHMARK.md)**. This folder holds the
code so an independent researcher can re-run everything with their own data.

There are **two** benchmarks here, because the two questions are different:

| Benchmark | Question it answers | LLM in the retrieval path? |
|---|---|---|
| **EnterpriseMemoryBenchmark** | Can the deterministic store find the *exact* record among thousands of near-identical ones, instantly and reproducibly? | **No** — pure index retrieval |
| **LongMemEvalAgent** | Can the agent answer conversational-memory questions by searching a document archive with `FileTool.FileSearch`? | Yes (agent + judge) |

The Enterprise benchmark is the one that matches how the system is actually used in
production. The LongMemEval-S harness is included for comparability with a known
academic benchmark, and is run in the way our architecture really works (the archive
*is* the long-term memory), not the way the original paper assumes.

---

## Prerequisites

- **.NET 10 SDK** (the harnesses reference `AIOrchestrator` as a project reference).
- **Python 3.8+** (only for the LongMemEval judge).
- An **OpenAI-compatible endpoint** for the agent and the judge (the Enterprise
  benchmark needs none — its retrieval is LLM-free).
- For LongMemEval-S only: the cleaned dataset from
  [huggingface.co/datasets/xiaowu0162/longmemeval-cleaned](https://huggingface.co/datasets/xiaowu0162/longmemeval-cleaned)
  (`longmemeval_s_cleaned.json`, ~277 MB). It is **not** committed here.

## Environment variables

| Variable | Used by | Required | Default |
|---|---|---|---|
| `SUPERFAST_API_KEY` | LongMemEvalAgent, judge.py | Yes | — |
| `SUPERFAST_BASE_URL` | both, judge.py | No | `http://127.0.0.1:8000/` |
| `SUPERFAST_MODEL` | both, judge.py | No | `gpt-4o-mini` |
| `LME_DATA` | LongMemEvalAgent | No | `longmemeval_s_cleaned.json` |
| `LME_OUT` | LongMemEvalAgent | No | `out` |
| `ENT_OUT` | EnterpriseMemoryBenchmark | No | `out` |

> **Never commit an API key.** Everything reads the key from the environment. The
> AgentBridge repository is public.

---

## 1. Enterprise benchmark (deterministic retrieval, no LLM)

Generates `N` synthetic, near-identical case files (insurance-style: a practice
number, a claimant name, a few fields), indexes them into the streaming word/vector
index, then issues key-addressed queries and measures recall, precision, latency and
determinism. Searching by name only (no key) is shown to be ambiguous on purpose.

```bash
cd EnterpriseMemoryBenchmark
dotnet run -c Release -- --records 1000 --queries 100 --seed 42 --out out
```

Output: `out/enterprise_results.json` with the metrics reported in
`docs/MEMORY-BENCHMARK.md` §4. Tune `--records` to scale the archive (the point of
the benchmark is that recall stays at 100% as the archive grows, because the key is
exact and no model is in the loop).

## 2. Architecture-faithful LongMemEval-S (agent + FileSearch)

For each instance the harness writes the haystack sessions as markdown documents into
a fresh sandbox, points `Setup.DocumentsPath` at them, waits for the index to go
idle, then runs the agent with `FileTool` under a prompt that states the answer is
somewhere in the archive and must be found with `file_search`. The agent's reply is
the hypothesis.

```bash
cd LongMemEvalAgent
# point at the downloaded dataset
set LME_DATA=C:\path\to\longmemeval_s_cleaned.json      # Windows
# export LME_DATA=/path/to/longmemeval_s_cleaned.json    # Linux/macOS

# quick smoke (1 instance) to confirm the pipeline works
dotnet run -c Release -- --smoke --out out

# small pilot: 2 instances per category
dotnet run -c Release -- --per-category 2 --out out

# shard across machines/processes (instance i goes to shard i % shard-count)
dotnet run -c Release -- --per-category 2 --shard-index 0 --shard-count 4 --out out0
dotnet run -c Release -- --per-category 2 --shard-index 1 --shard-count 4 --out out1
# ... then concatenate the hypotheses.jsonl files
```

Key flags: `--per-category N`, `--categories single-session-user,knowledge-update`,
`--max-sessions M` (cap the haystack), `--max-iterations` (agent loop budget).

This produces `out/hypotheses.jsonl` (one `{"question_id","hypothesis"}` per line).

### Judge

Grade the hypotheses with the official LongMemEval per-category prompts:

```bash
python judge.py --data C:\path\to\longmemeval_s_cleaned.json \
               --hyp  out/hypotheses.jsonl \
               --out  out/judge_results.json
```

`judge.py` prints a per-category and overall accuracy table and writes
`out/judge_results.json`.

> **Comparability caveat.** The official LongMemEval judge is GPT-4o. If you judge
> with a different model, absolute numbers are not directly comparable to published
> GPT-4o-judged figures, but they are internally consistent across every system
> judged the same way. See `docs/MEMORY-BENCHMARK.md` §7 for the full list of
> limitations.

---

## What each mechanism actually exercises

- **EnterpriseMemoryBenchmark** → the deterministic `NameOrKey` store + the
  streaming word index. This is the production retrieval path for "find the record
  by its key". No model, no embedding cost, fully reproducible.
- **LongMemEvalAgent** → the *document-archive* memory mechanism: content is
  classified into the sandbox as markdown, indexed, and retrieved by the agent via
  `FileTool.FileSearch` + `ReadFile`. This is the mechanism used when the answer is
  prose buried in the archive rather than a keyed fact.

The other two long-term-memory mechanisms (Skills memory and the Scheduler) are
procedural and are not exercised by these Q&A benchmarks; see
`docs/MEMORY-BENCHMARK.md` §1 for the full four-mechanism model.
