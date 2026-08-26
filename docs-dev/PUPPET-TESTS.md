# Puppet Tests — Index (ToC)

End-to-end tests that drive the **AgentBridge TUI automatically** through the
[puppet mode](PUPPET-MODE-GUIDE.md) (DEBUG-only TCP surface on `localhost:5291`:
screen capture + key/text/mouse injection, exactly what a real user can do).

This document is the **table of contents of the launchable puppet tests**. It lists
every available test with a short explanation of what it checks, how to run it and
what a passing run looks like.

> **MEMO — project rule:** every NEW puppet test added to this repository MUST be
> registered here, with a short explanation (name, scenario, command, verification),
> so this index stays the single place to discover what can be launched. A new test
> without an entry in this document is incomplete.

## Common prerequisites

- **DEBUG build** of `agent.exe` (the puppet listener exists only under `#if DEBUG`):
  `dotnet build AgentBridge.csproj -c Debug --no-incremental`.
- The **LLM provider is reachable** — the DeepSeekBridge on `127.0.0.1:8787` (or
  another configured provider in `providers.json`); the agent needs an LLM.
- **Port `5291` free** (no other agent instance running).
- A fresh `Tools/` state: the build copies the plugins automatically (see the
  `ShipToolPlugins` target — the plugin's own assemblies are always refreshed).
- The harnesses launch `agent.exe --enable-log --SkipIndexingOnStartup true
  --no-update --tui` in its own console window and wait for the session to be ready.

## Available tests

| # | Test | What it checks | Command |
|---|------|----------------|---------|
| 1 | **PuppetSheet** (`e2e/PuppetSheet`) | The agent creates a **spreadsheet** (single English worksheet: small dataset + one chart, A4 page setup) from a plain prompt. The harness verifies the produced `.xlsx` structurally: all-sheet data cells, namespace-aware chart series, A4 page setup on the data sheet, well-formed XML — and reports the file path. | `dotnet run --project e2e\PuppetSheet` (optional `--agent-exe <path>`, `--keep`, `--smoke`) |
| 2 | **PuppetDocs** (`e2e/PuppetDocs`) | The agent creates **office documents** (invoice + employment contract) from the OfficeSupportTool templates, with the material attached via `/files add`; verifies the `.docx` content. | `dotnet run --project e2e\PuppetDocs` (optional `--agent-exe <path>`, `--keep`) |
| 3 | **TuiSmoke** (`e2e/TuiSmoke`) | Smoke test of the terminal UI itself via a Windows **pseudoconsole (ConPTY)**: logo + input line render, the model picker opens and closes, a chat message is sent. | `dotnet run --project e2e\TuiSmoke [agent-exe] [base-url]` |

## Verification pattern

A passing puppet test exits with code `0` and prints the produced artifacts
(`XLSX saved to: ...`, `DOCX written to: ...`) plus the structural checks (`✓`).
Diagnostics:

- agent log: `logs\<pid>.txt` — tool calls, receipts, `TUI chat finished`;
- TUI screen dumps: `tui-screenshots\puppet-<timestamp>.txt` (PrintScreen);
- per-test results file in `%TEMP%` (e.g. `puppetsheet_results.txt`).

## Regression notes (learned while building these tests)

- **Never start a run before the plugin copy has completed** — rebuild the host first
  and check `Tools\` timestamps; a stale plugin in the bin shows old behaviour.
- **Do not type into the agent window while a puppet run is active** — injected input
  mixes with real keystrokes (a stray keypress can garble the `/agent` checklist).
- The agent's behaviour is **stochastic** (LLM): a run may occasionally skip a step or
  duplicate a block. Re-run to distinguish model variance from a real bug; the
  structural verification catches both.
- `--SkipIndexingOnStartup` must be passed to keep the document-index watcher off the
  test workspace (otherwise it converts the agent-created files and can surface
  conversion warnings on stderr).

See [PUPPET-MODE-GUIDE.md](PUPPET-MODE-GUIDE.md) for the full protocol, the reference
client (`tools/puppet.ps1`) and the debugging procedure.
