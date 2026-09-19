# Long-Term Memory: Architecture, Benchmarks, and Honest Comparison

This document explains how AIOrchestrator / AgentBridge long-term memory works, what we
measured, and how it compares to academic benchmarks and to other memory systems that
have been widely discussed. We report only numbers we actually measured, and we label
clearly what is measured here versus what is self-reported by others.

## 1. The memory model

AIOrchestrator does not have one memory. It has **four complementary long-term memory
mechanisms**, each built for a real operating need:

1. **NameOrKey memory** — a deterministic, key-addressable store. At the end of a
   conversation the system extracts the important entities (proper names, codes,
   document/practice numbers, ids) and stores a short fact under each key. Reading is a
   deterministic key lookup injected into the prompt. No vector search, no LLM call at
   read time.
2. **Document archive (sandbox) + streaming word/vector index** — the real long-term
   memory for an enterprise. Documents placed in the sandbox are converted to markdown
   and indexed incrementally (a streaming word index plus per-document vectors). The
   agent retrieves them with `FileTool.FileSearch` (by NameOrKey elements, keywords,
   path, date) and reads them with `ReadFile`.
3. **Skills memory** — reusable procedures learned from conversations.
4. **Scheduler memory** — procedural memory: a recurring task the user asked for is
   stored and executed on schedule, exactly as instructed.

The design principle: **long-term memory lives in the archive, not in the chat transcript.**
Most of what is said in a chat is ephemeral and not worth keeping. What matters — the
document a chat caused to be created, the practice number, the contract, the decision —
is classified into the archive and indexed for deterministic retrieval.

## 2. Why the standard LongMemEval-S run does not fit this architecture

LongMemEval-S (ICLR 2025) is a legitimate, peer-reviewed benchmark of episodic /
long-context memory. Each instance is a long chat "haystack" (~48 sessions, ~115k
tokens) and a natural-language question, usually **without a key**. The benchmark
assumes **semantic** retrieval: match the question to the relevant session by meaning.

Our NameOrKey memory is **key-addressable**, not semantic. Retrieval needs the query to
share a capitalized proper-noun or digit token with the stored key. A keyless question
like *"What degree did I graduate with?"* produces **zero keys**, so the deterministic
store surfaces nothing.

Measured (single instance, `e47becba`):

| Step | Result |
|---|---|
| Memory populated from the haystack | 150+ correct keyed entries |
| Keys extracted from the question | 0 |
| Memory surfaced | 0 entries |
| Answer | "I do not know" |
| Gold answer | "Business Administration" |

This is **not a bug**. It is the nature of a key-addressable store: it answers
"what is the status of practice PR-100042" instantly, but not "what degree did I
graduate with" when the question carries no key. LongMemEval-S measures a retrieval
paradigm (semantic, keyless) that this mechanism deliberately does not implement.

## 3. LongMemEval-S done the architecturally-faithful way

In our architecture, "search memory" for this kind of content means: the data is
classified into the document archive, indexed, and the **agent finds it with
`FileTool.FileSearch`**. So we re-ran LongMemEval-S that way:

- Each haystack session is written as a markdown document into a fresh sandbox.
- The sandbox is indexed into the streaming word/vector index.
- The agent is run with `FileTool` and a prompt stating the answer is in the archive
  and must be located with `FileSearch` + `ReadFile`.
- Answers are judged with the exact LongMemEval per-category prompts.

**Pilot result (14 instances, 2 per category), judged by the same model used for
answering (halogen-qwen; note: the official judge is GPT-4o, so absolute numbers are
not directly comparable to published figures, but internally consistent):**

| Category | Correct |
|---|---|
| abstention | 2 / 2 |
| single-session-assistant | 2 / 2 |
| single-session-user | 1 / 2 |
| single-session-preference | 1 / 2 |
| temporal-reasoning | 1 / 2 |
| knowledge-update | 1 / 2 |
| multi-session | 0 / 2 |
| **Overall** | **8 / 14 (57.1%)** |

This is a real number from a small pilot. It shows the agent + `FileSearch` path
reaches facts the key-addressable store cannot, and is the correct way to evaluate this
architecture on LongMemEval. It is **preliminary** (small N) and has honest weak spots
(multi-session aggregation, some temporal/preference cases).

## 4. The Enterprise real-case benchmark

This is the scenario the lab benchmarks ignore and where the architecture is built to
win: a company archive of **many near-identical records** that differ only by a key
element (a practice number, contract id, patient id). In a real company there are
thousands to millions of such records, and the user always references a key.

We generated a **synthetic** archive (no private data, reproducible via a seed) of
1,000 near-identical insurance case files, each differing by practice number,
claimant, amount, date, status. We then measured the deterministic retrieval path
(`FileTool.FileSearch` by the digit-bearing practice number). **No LLM is used in the
retrieval path.**

**Measured (1,000 near-identical records, 100 key-addressed queries, seed 42):**

| Metric | Value |
|---|---|
| Indexing time (1,000 docs) | 23.3 s |
| Key-addressed recall | **100%** (100 / 100) |
| Precision (files returned per query) | **1.00** (exact, no noise) |
| Mean latency per query | 228 ms (no LLM) |
| Determinism (identical across 2 runs) | **100%** |
| Name-only search (no key) | 20 records returned → ambiguous |

The last row is the point: searching by a common claimant name returns many records
(ambiguous). Adding the key (the practice number) returns exactly one, every time,
instantly, with no LLM. This is the "which Andrea Rossi / which practice" problem that
real enterprise archives create and that key-addressable deterministic retrieval solves.

## 5. Comparison

Numbers marked **measured here** are from this repository's harnesses. Numbers marked
**self-reported** are published by their authors and were **not** reproduced by us;
treat them with caution.

| System / path | Setting | Headline result | Source |
|---|---|---|---|
| NameOrKey memory (keyless NL) | LongMemEval-S | ~0% (no key → no retrieval) | measured here |
| Agent + FileSearch (archive) | LongMemEval-S pilot | 57.1% (8/14) | measured here |
| Deterministic key retrieval | 1,000 near-identical enterprise records | 100% recall, 1.00 precision, 228 ms, 100% deterministic | measured here |
| GBrain (cloud, large models) | self-reported | self-reported, contested; not reproduced here | self-reported |
| MemPalace | self-reported | self-reported, contested; not reproduced here | self-reported |

We deliberately do **not** claim to have beaten these systems on their own benchmark.
We could not reproduce their pipelines, and their figures are self-graded. What we can
state honestly is measured above.

## 6. Scaling argument (analytical, not measured)

The decisive enterprise difference is **cost and determinism at scale**, which no
small lab benchmark measures:

- **Our indexing** is O(N) word/key extraction with no model call. Measured: ~23 ms per
  document (23.3 s for 1,000). Extrapolating linearly, 1 million documents index in
  roughly hours on one machine, with no token cost.
- **Dense-embedding / LLM-semantic indexing** requires a model call per document.
  At a typical ~500–1,000 tokens per document and a commercial embedding price,
  indexing 1 million documents costs hundreds of dollars and many hours; at
  enterprise scale (tens of millions of documents, terabytes) it becomes a
  multi-day, high-cost job — and the result is non-deterministic (model- and
  version-dependent), and near-duplicate records collapse together in vector space,
  which is exactly the ambiguity a practice number is meant to remove.
- **Our retrieval** is a deterministic index lookup (228 ms measured, no LLM),
  reproducible run-to-run. A semantic top-k over near-identical records is neither
  deterministic nor precise without the key.

This is stated as an analytical extrapolation with explicit assumptions, not a
measured TB-scale run (we do not have terabytes of private enterprise data to expose).

## 7. Honest limitations

- The LongMemEval-S faithful result is a **14-instance pilot** (57.1%), not a full
  500-instance run. Multi-session aggregation is a known weak spot.
- Our judge is the same halogen model used for answering, not GPT-4o; absolute
  LongMemEval numbers are internally consistent but not directly comparable to
  published GPT-4o-judged figures.
- The Enterprise benchmark uses **synthetic** near-identical records, not real
  company data (we will not expose private data). The mechanism and metrics are real;
  the data is synthetic by design and is meant to be replaced by a researcher's own
  archive.
- We did **not** reproduce GBrain / MemPalace. Their numbers are self-reported and
  contested.
- The scaling section is analytical extrapolation, not a measured TB-scale run.

## 8. Reproducing

See `benchmark/README.md` for exact commands. In short:

- `benchmark/LongMemEvalAgent/` — architecture-faithful LongMemEval-S (agent +
  `FileSearch`). Set `SUPERFAST_API_KEY` (and optionally `SUPERFAST_BASE_URL`,
  `SUPERFAST_MODEL`) to your OpenAI-compatible provider.
- `benchmark/EnterpriseMemoryBenchmark/` — synthetic enterprise archive +
  deterministic retrieval metrics.
- `benchmark/judge.py` — LongMemEval per-category judge.

## 9. Bottom line

LongMemEval-S is a good benchmark for a **personal-assistant, keyless, semantic**
memory scenario. It is **limited** for evaluating an **enterprise, key-addressable,
deterministic** memory. Our system is built for the latter: multiple memory
mechanisms, deterministic retrieval over large archives, update-wins, near-zero
read cost, and reproducibility — properties that matter in real enterprise
environments with large, redundant data, and that lab benchmarks with small,
keyless datasets do not capture.
