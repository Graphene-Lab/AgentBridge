# Crash reporting — privacy-safe diagnostics to the GitHub repository

When AgentBridge terminates with an unhandled exception, it offers a **sanitized** crash
diagnostic to the project's GitHub issues so crashes can be fixed without chasing logs.

## What is sent (and what is not)

The automatic report is a static text payload built **only** from the crash's `Exception`
metadata, in-process. It contains:

| Sent | Never sent |
|---|---|
| The exception **type chain** (e.g. `System.IO.IOException → System.Net.Sockets.SocketException`) | Exception **messages** (the most likely place for user content: prompts, file paths, provider errors) |
| The **stack-trace frames that belong to AgentBridge code only** (method names, file names, line numbers of the `AgentBridge`, `AIOrchestrator`, `UISupportGeneric` and `Graphene.*` plugin assemblies) | Memory dumps, heap snapshots, request bodies, session data, chat history, configuration, API keys, IP addresses, timestamps, usernames |
| The AgentBridge version | Anything else |

That is the "description obtained with `Exception.ToString()`" limited to its
code-reference part — the exception message is deliberately dropped because it is the one
field that can carry user data.

**How to verify (privacy by construction):** the payload is assembled by
`CrashReporter.BuildReport` in `CrashReporter.cs` — roughly twenty lines that read only
`Exception.GetType()` and `Exception.StackTrace`, filtering frames whose namespace is not
one of ours. You can also see the exact text before it leaves the machine: when the report
is delivered through the browser (no token, no `gh`), the **pre-filled GitHub issue page
shows the full text and nothing is submitted until you press "Submit new issue"**.

## How the report is delivered

Automatic, best-effort, in this order:

1. **GitHub token configured** (`CrashReport:Token` in appsettings.json) → the report is
   posted to the repository issues API (`CrashReport:Repo`, default
   `Graphene-Lab/AgentBridge`). The token is read only from the machine's own
   appsettings.json and is never included in any report.
2. **`gh` CLI installed and authenticated** (the machine's own GitHub login — no token
   stored) → the issue is created automatically with `gh issue create`.
3. **Desktop session** (no token, no `gh`) → the pre-filled *new issue* page opens in the
   OS default browser: the user reviews the exact text and submits it with one click.
4. **Headless server** (no token, no `gh`) → nothing is sent; the crash remains in the
   local log (`logs/<pid>.txt`) as before.

Sending never blocks the process for long (bounded timeouts), never fails the crash
handling itself, and never touches the report payload with any configuration.

## Disabling the reports

- **TUI**: menu **Help → Crash report** (or `/crashreport`) toggles sending; the state is
  shown in the menu title and persists in the OS app-data folder (same tier as the
  auto-update toggle — updates never touch it).
- **appsettings.json**: `"CrashReport": { "Enabled": false }` disables it for a fresh
  install; the persisted TUI toggle wins once set.

Disabling stops only the **sending** — the local crash log is unchanged.

## Repository setup

The target repository (`Graphene-Lab/AgentBridge`, public) has issues enabled and ships a
dedicated **Crash report** issue template (`.github/ISSUE_TEMPLATE/crash_report.yml`,
label `crash`) that documents the sanitized content and prompts for the version and the
optional context.
