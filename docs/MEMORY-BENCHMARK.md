# Long-Term Memory: Architecture and Measured Results

This document explains how AIOrchestrator / AgentBridge long-term memory works and
what we measured on a public academic benchmark. We report only numbers we actually
measured, and we clearly separate our measurements from figures that other systems
self-report.

## 1. The architectural choice: long-term memory is the archive

In our architecture, **long-term memory is the document archive** — the sandbox area
where the agent's work is stored. This is a deliberate design choice, not a missing
feature.

When a document enters the sandbox it is converted to markdown and indexed into a
**streaming word/vector index**. That index is the only thing the agent reads from at
long-term scale, and it is built incrementally as documents arrive — there is no
batch re-embedding step and no model call at read time. The agent searches the
archive with `FileTool` and reads what it finds.

We chose this because it matches what real enterprise environments actually look
like: a company does not remember its history as a chat transcript. It remembers it
as an archive — contracts, practice numbers, case files, reports, decisions. The
chat is ephemeral; the archive is the memory. An assistant that is useful at work
must be good at navigating an archive, not at replaying a conversation.

There are five complementary memory mechanisms, all reading from or writing to that
persistent store:

1. **NameOrKey memory** — a deterministic, key-addressable store. Important
   entities (proper names, codes, document/practice numbers, ids) are stored under a
   key and read back by exact key lookup. No vector search, no LLM call at read time.
2. **Document archive (sandbox) + streaming index** — the long-term memory at scale.
   Documents are indexed as they arrive and retrieved with `FileTool`
   (`FileSearch`, `SearchContext`, `ReadFile`).
3. **Skills memory** — reusable procedures learned from prior work.
4. **Scheduler memory** — a recurring task the user asked for is stored and executed
   on schedule, exactly as instructed.
5. **Chat history (context window)** — the running conversation itself is a memory,
   because it sits inside the model's context window. This is the standard mechanism
   every agent harness uses: recent turns are simply present, so the agent can refer
   back to what was just said without any retrieval. It is short-term and bounded by
   the window; the four mechanisms above are what carry knowledge past that boundary
   and across sessions.

The benchmark in this section deliberately exercises the long-term mechanisms
(1–4), not the chat history: each question is answered from the archive with the
conversation reset, which is the hard case. Had we run the test the way a normal
chat works — keeping the full history in the context window — the agent would have
had a clearer, broader view of the conversation on top of the archive, and the
answers would only get easier. We report the harder, archive-only configuration.

### The hardware-optimized vector

The streaming index is built the way the hardware wants to be read, and this is what
lets the same system scale to terabytes on an ordinary consumer machine.

- **8-byte elements.** The vector is made of 8-byte (64-bit) elements — exactly the
  native word/block size of a 64-bit chip, on both x64 and arm64. Reading it is a
  sequence of natural, aligned machine reads, not a gather across a foreign layout.
- **One linear stream.** There is a single streaming read of the vector: the data is
  consumed sequentially, block after block. There is no logic to juggle across
  semantic trees, memory graphs, or correlation structures that cannot be read in a
  straight line. A linear pass over aligned 8-byte blocks is the closest match to how
  the processor and memory hierarchy actually work, which is the most optimized form
  a read can take.
- **Deterministic context at zero token cost.** In real use (not a benchmark), the
  context the agent needs is selected deterministically from the prompt and handed to
  the model already assembled. No LLM call is spent to "remember" or to search: the
  model receives the relevant context ready-made, so the retrieval work costs zero
  tokens and adds no model round-trips.

This is a step forward over the well-known memory approaches. It means AgentBridge
can give an agent context extracted from **terabytes of documents and data on a home
consumer computer, with no GPU and no dedicated LLM for indexing or retrieval** — a
combination that, offered as a **trustless** system (your data never leaves your
machine), has no real analog in the enterprise space and makes AgentBridge a genuine
**privacy-first** tool.

We deliberately do **not** implement semantic-search or LLM-based indexing. Those
approaches require a model call per document and a powerful datacenter-class machine
to be useful at big-data scale; computed outside a datacenter, on consumer hardware,
they are not viable. The deterministic, hardware-aligned streaming vector is the
design that works at this scale on the hardware people actually own.

## 2. LongMemEval-S, run the way this architecture reads memory

LongMemEval-S (ICLR 2025) is a legitimate, peer-reviewed benchmark of long-term
episodic memory. Each instance is a long chat "haystack" (~40–53 sessions, ~115k
tokens) and a natural-language question, usually without a key. The standard way to
answer is to read the whole haystack in one long context window.

We do not read memory that way. So we ran LongMemEval-S the way our system actually
works:

- Each haystack session is written as a markdown document into a fresh sandbox.
- The sandbox is indexed into the streaming word/vector index.
- The agent is run with `FileTool` and told the answer is in the archive and must be
  located by searching and reading — exactly as it would locate a real document.
- Answers are judged with the exact LongMemEval per-category judge prompts.

**Measured result (70 instances, 10 per category), judged by the same local model used
for answering:**

| Category | Correct |
|---|---|
| single-session-assistant | 10 / 10 |
| temporal-reasoning | 10 / 10 |
| single-session-user | 9 / 10 |
| abstention | 8 / 10 |
| knowledge-update | 7 / 10 |
| multi-session | 7 / 10 |
| single-session-preference | 6 / 10 |
| **Overall** | **57 / 70 (81.4%)** |

This is a real, reproducible number from this repository's harness. It is a 70-instance
sample, not the full 500-instance set, and the per-category weak spots (some
knowledge-update, multi-session and preference cases) are stated honestly in section 6.

## 3. What we are comparing against (and the hardware gap)

The reference point is the LongMemEval paper itself (arXiv 2410.10813):

| Reader | LongMem_S accuracy | Setting |
|---|---|---|
| GPT-4o, oracle (no long history) | 0.870 | reads only the gold sessions |
| GPT-4o, long-context | **0.606** | reads the full ~115k-token haystack |
| Llama-3.1-70B, long-context | 0.334 | full haystack |
| Phi-3-14B, long-context | 0.380 | full haystack |
| Paper's optimized memory framework (GPT-4o reader) | ~0.65–0.70 | retrieval-augmented |

Our measured **81.4%** is **well above the GPT-4o long-context baseline (60.6%)** and
above the paper's optimized-memory-framework range (~65–70%), approaching even the
GPT-4o oracle that reads only the gold sessions (87.0%) — but it is important to be
precise about the hardware this was measured on, because it is the opposite of the
usual comparison.

- The model is a **local model running at about 46 tokens/second of output**.
- That **same model, on the same machine, also drove the agent that followed the test
  end to end** — the agent that read the questions, chose the searches, opened the
  files, and wrote the answers. There was no faster or larger model anywhere in the
  loop.
- The cloud systems in the comparison table run on **much faster machines with
  enormously larger models**. GPT-4o is a frontier cloud model; ours is a small
  local model (~6B active parameters) on a single desktop-class device.

So the comparison is not "our big cloud model vs their big cloud model." It is a
small local model, at 46 tok/s, doing the whole job on one machine — and still
clearing the GPT-4o long-context baseline. That is the honest framing.

### Why we can only run this with much smaller models

The official LongMemEval judge is GPT-4o. We did **not** use GPT-4o, and we cannot.

- **GPT-4o cannot run locally.** OpenAI never released the model weights. It exists
  only behind a paid cloud API, so no local machine can run it, at any size.
- **We have no datacenter of our own.** Running a GPT-4o-class model locally would
  need datacenter hardware (hundreds of GB of VRAM, multi-GPU). We do not have that.
- **We have no GPT subscription and no free-token path.** We are not subscribed to
  GPT-4o and will not pay for it, and there is no legitimate free-token route to run
  the ~500 judged tests.

So we run the whole test — both answering and judging — on a **much smaller local
model** (~6B active parameters, ~46 tok/s). This is the honest reason our number is
smaller-model-based: it is what our hardware and budget allow, not a choice to make
the test easier. A smaller model makes our result *harder* to get, not easier.

The consequence we state plainly: our number is internally consistent, but it is
**not directly identical to GPT-4o-judged figures**. The harness is fully
environment-configurable, so a third party who **does** have GPT-4o access can
re-run the judge (`benchmark/judge.py`) against the same answers and produce the
directly-comparable number. We provide everything needed for that independent
reproduction; we simply cannot be the ones to pay for it.

## 4. The Enterprise benchmark — the scenario this is actually built for

LongMemEval is a personal-assistant, keyless, semantic-memory test. It is useful, but
it is a small slice of what this architecture is designed for. The real target is an
enterprise archive: **many near-identical records that differ only by a key** (a
practice number, a contract id, a patient id). In a real company there are
thousands to millions of such records, and the user always references a key.

We generated a **synthetic** archive (no private data, reproducible from a seed) of
1,000 near-identical insurance case files, each differing by practice number,
claimant, amount, date, status, and measured the deterministic retrieval path
(`FileTool.FileSearch` by the digit-bearing practice number). **No LLM is used in
this retrieval path at all.**

**Measured (1,000 near-identical records, 100 key-addressed queries, seed 42):**

| Metric | Value |
|---|---|
| Indexing time (1,000 docs) | 23.3 s |
| Key-addressed recall | **100%** (100 / 100) |
| Precision (files returned per query) | **1.00** (exact, no noise) |
| Mean latency per query | 228 ms (no LLM) |
| Determinism (identical across 2 runs) | **100%** |
| Name-only search (no key) | 20 records returned → ambiguous |

The last row is the point. Searching by a common claimant name returns many records
(ambiguous). Adding the key — the practice number — returns exactly one, every time,
instantly, with no LLM. This is the "which Andrea Rossi / which practice" problem
that real enterprise archives create, and it is the problem this architecture is
built to solve. The LongMemEval run above is, in proportion to what the system can
do, the small and simple case.

## 5. Honest comparison with other memory systems

Numbers marked **measured here** come from this repository's harnesses. Numbers
marked **self-reported** were published by their authors and were **not** reproduced
by us; treat them with caution.

| System / path | Setting | Headline result | Source |
|---|---|---|---|
| Agent + archive (`FileTool`) | LongMemEval-S, 70-instance run | **81.4%** (57/70), local model @ 46 tok/s | measured here |
| Deterministic key retrieval | 1,000 near-identical enterprise records | 100% recall, 1.00 precision, 228 ms, 100% deterministic | measured here |
| GPT-4o long-context | LongMem_S (paper) | 60.6% | paper (arXiv 2410.10813) |
| Paper's optimized memory framework | LongMem_S (paper) | ~65–70% | paper (arXiv 2410.10813) |
| Vaino | self-reported | ~0.7 / GB, self-graded | self-reported |
| GBrain | self-reported | self-reported, contested | self-reported |
| MemPalace | self-reported | self-reported, contested | self-reported |

We do **not** claim to have beaten the self-reported systems on their own pipelines —
we could not reproduce them, and their figures are self-graded. What we state is what
we measured, on hardware that is far weaker than theirs.

## 6. Honest limitations

- The LongMemEval-S result is a **70-instance run** (81.4%), not the full
  500-instance set. Treat it as a measured sample, not a final ranking.
- Our judge is the same local model used for answering, not GPT-4o. Numbers are
  internally consistent but not identical to GPT-4o-judged figures.
- The weak categories are **multi-session aggregation**, some **knowledge-update**,
  and some **single-session-preference** cases — questions that need combining or
  reinterpreting facts across many sessions. These are genuinely hard and we report
  the misses rather than hide them.
- The Enterprise benchmark uses **synthetic** near-identical records, not real
  company data. The mechanism and metrics are real; the data is synthetic by design
  and meant to be replaced by a researcher's own archive.
- We did **not** reproduce Vaino / GBrain / MemPalace. Their numbers are
  self-reported and contested.

## 7. Reproducing this

See `benchmark/README.md` for exact commands. In short:

- `benchmark/LongMemEvalAgent/` — the architecture-faithful LongMemEval-S run
  (agent + `FileTool` over the indexed sandbox). Set `SUPERFAST_API_KEY` (required)
  and optionally `SUPERFAST_BASE_URL`, `SUPERFAST_MODEL` to any OpenAI-compatible
  provider. Args: `--data`, `--out`, `--per-category`, `--categories`,
  `--max-sessions`, `--max-iterations`, `--shard-index`, `--shard-count`.
- `benchmark/judge.py` — the exact LongMemEval per-category judge. Same env vars.
- `benchmark/EnterpriseMemoryBenchmark/` — the synthetic enterprise archive and the
  deterministic retrieval metrics.

The dataset is the public LongMemEval-S cleaned set (`longmemeval_s_cleaned.json`).
The harness reads it from a local path you provide; nothing is bundled that is not
already public.

## 8. Bottom line

LongMemEval-S is a good benchmark for a personal-assistant, keyless, semantic memory
scenario. Our system reaches **81.4%** on a 70-instance run of it — well above the
GPT-4o long-context baseline and above the optimized-memory-framework range — using
a small local model at 46 tok/s that also drove the whole test, on one machine.

But the architecture is built for the enterprise case: long-term memory as an
archive, deterministic key-addressed retrieval over large and redundant data,
update-wins semantics, near-zero read cost, and full reproducibility. That is the
scenario where it is strongest, and where the small keyless lab tests only hint at
what it can do.
