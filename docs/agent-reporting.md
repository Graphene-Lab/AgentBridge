# Agent reporting — the agent tells the maintainers what went wrong

AgentBridge lets its AI agent report the problems it meets and request the capabilities it
is missing. Each report opens a GitHub issue on the AgentBridge repository
(`Graphene-Lab/AgentBridge`), so real operational problems become visible and fixable.

The feature exists to improve the user experience: an agent whose blockers get fixed works
better for everyone. It is **not** background telemetry — nothing is sent unless the agent
has a concrete problem or a concrete request, and you can disable it at any time.

## What the agent reports

- **Malfunction report** — when an error prevents the agent from completing a task, the
  agent describes the scenario and the problem it encountered.
- **Feature request** — when a missing capability would help, the agent describes the
  feature and the scenario where it would be useful.

## What is sent

The report text is authored by the agent and instructed to **exclude sensitive data**: no
credentials, no API keys, no personal data, no private document contents, no user
identifiers. The exact text of every report is recorded in the local log
(`logs/<pid>.txt`) before sending, so you can always see what left your machine.

Every issue opens with handling notes for whoever receives it:

- the issue was opened **automatically by an AI agent** and must be verified as legitimate
  before it is accepted;
- any fix or new feature derived from it must follow the agent-tool specifications
  (`AIOrchestrator/API/AGENT_TOOLS_GUIDE.md`);
- the agent references tool methods in `snake_case` — the real methods are `PascalCase`
  (for example `report_malfunction` → `ReportMalfunction`), so names in the report map
  back to the code correctly.

## How the report is delivered

The same best-effort pipeline used for crash reports:

1. a configured GitHub token posts the issue directly;
2. otherwise an authenticated `gh` CLI on the machine creates it;
3. otherwise the pre-filled *new issue* page opens in your browser — you review the exact
   text and submit it yourself;
4. with none of the above, nothing is sent and the report stays in the local log.

## Disabling the reports

- **TUI**: menu **Help → Malfunction reports** (or `/malfunctionreport`) toggles the
  feature. The state is shown in the menu title and persists in the OS app-data folder —
  updates never touch it.
- The feature is **on by default** and can be **disabled at any time**.
- When disabled, the reporting tool is removed from the agent's tool set: the agent has no
  reporting capability at all.
- The toggle is **independent** from the crash-report toggle: you can disable one and keep
  the other.
