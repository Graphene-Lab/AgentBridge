# AgentBridge → AIOrchestrator → tools

AgentBridge **hosts** (references) the `AIOrchestrator` library (which is not directly
executable on its own). Inside the engine, `AgentHarness` is the per-conversation agent: it
resolves a tool set, runs the agent loop (with subagent) and returns the result. The tools split
into **system tools** (compiled into the AIOrchestrator assembly, always in front) and
**plugin tools** (loaded dynamically from the `Tools/` folder).

```mermaid
flowchart TD
  subgraph host["AgentBridge / AIOffice (host)"]
    program["Program.cs / AIOffice panels"]
    plugins["ToolPlugins.Host<br/>(McpToolHost)<br/>scan Tools/ + watcher"]
  end

  subgraph engine{"AIOrchestrator (library, not directly executable)"}
    harness{{"AgentHarness<br/>(per-conversation agent)"}}
    reg["McpToolRegistry<br/>ResolveAll(name)"]
    base["BaseAgentTool<br/>common contract"]
    sys1["FileTool<br/>(sandbox search/read)"]
    sys2["GitTool<br/>(versioning/rollback)"]
    sys3["TaskSchedulerTool<br/>(scheduled chats)"]
    sys4["WebTool"]
    sys5["EMailTool"]
    subagent{{"launch_subagent<br/>(isolated LLM session)"}}
  end

  subgraph pluginslib["Plugin tool (Tools/<Plugin>/)"]
    pl1["DocumentTool"]
    pl2["SpreadsheetTool"]
    pl3["PresentationTool"]
    pl4["OfficeTool"]
    pl5["FreeCADTool"]
    pl6["ErpTool / OdooTool / ..."]
  end

  program -->|"appends plugin types to the built-in set"| plugins
  program -->|"ExecuteAction / ExecuteActionStream"| harness
  plugins -->|"registers plugin tool (Assembly.Load(byte[]))"| reg
  reg -->|"resolve name -> Type"| base

  harness -->|"in front (system tool)"| reg
  reg --> sys1
  reg --> sys2
  reg --> sys3
  reg --> sys4
  reg --> sys5

  harness -->|"lean orchestrator:<br/>AgentTools.SplitForOrchestration"| subagent
  subagent -->|"delegates (plugin tool, to create something complex)";| pl1
  subagent --> pl2
  subagent --> pl3
  subagent --> pl4
  subagent --> pl5
  subagent --> pl6

  classDef hostC fill:#dbeafe,stroke:#2563eb,stroke-width:1.5px,color:#172554
  classDef engC fill:#e0e7ff,stroke:#4f46e5,stroke-width:1.5px,color:#312e81
  classDef sysC fill:#dcfce7,stroke:#16a34a,stroke-width:1.5px,color:#14532d
  classDef plC fill:#f5f3ff,stroke:#7c3aed,stroke-width:1.5px,color:#3b0764
  class program,plugins hostC
  class harness,reg,base,sys1,sys2,sys3,sys4,sys5,subagent engC
  class pl1,pl2,pl3,pl4,pl5,pl6 plC
```

## System tools vs. plugin tools

| Level | Tools | Where they live | Default | Changeable from the TUI? |
|---|---|---|---|---|
| **Core** (always-on) | `FileTool`, `GitTool`, `TaskSchedulerTool` | Inside the AIOrchestrator assembly (`API/`) | Always ON | No — locked (separate read-only line in the picker) |
| **Class A** (our plugins) | `DocumentTool`, `SpreadsheetTool`, `PresentationTool`, `OfficeSupportTool`, … | `Tools/<Plugin>/` | ON at first start ("unspecified => ON" rule) | Yes (per-tool config) |
| **Class B** (vendored engine behind an adapter) | `OfficeTool` | `Tools/<Plugin>/` | OFF unless enabled | Yes (per-tool) |

The **core tools are architectural primitives**: `FileTool` is the sandbox search/read surface
(`FileSearch`, `AnswerQuery`, `GetDirectoryTree`); `GitTool` owns versioning and rollback (plugins
snapshot through `GitSupport`); `TaskSchedulerTool` owns scheduled automated chats (timers + JSON
persistence in `/.taskscheduler`, run logs in `/.taskscheduler/logs`). Removing them would
paralyze the tools that depend on them, so they stay ON and are changeable only by editing the
config file.

## Lean orchestrator (system tools in front, plugins behind a subagent)

> Design decision (2026-09-18). The cost of the catalog is paid **only when the work needs it**:
> the host divides what a preset resolves to — the **system tools** stay on the orchestrator, the
> **plugin tools** move behind a subagent (`AgentTools.SplitForOrchestration`, called at every
> `ExecuteAction`). The system tools are those compiled into the AIOrchestrator assembly
> (`FileTool`, `GitTool`, `TaskSchedulerTool`, `WebTool`, `EMailTool`); the plugins arrive from
> `Tools/`. The classification is **computed**, not listed:
> `type.Assembly == typeof(BaseAgentTool).Assembly`.

```mermaid
flowchart LR
  preset["Preset resolved<br/>(e.g. all-files)"] --> split{"AgentTools.SplitForOrchestration"}
  split -->|"system tool (simple, immediate requests, no round-trip)"| main["Main orchestrator<br/>(FileTool, GitTool, WebTool, EMailTool, ...)"]
  split -->|"plugin tool (to build something complex)"| sub{"launch_subagent"}
  sub -->|"full tool set (self-sufficient)"| pluginTool["DocumentTool / SpreadsheetTool / ..."]
  main -.|"no plugin loaded -> not split"| main

  note["Not split when: there is no loaded plugin tool / the set is a pure dispatcher.<br/>Kill-switch: AGENTBRIDGE_ORCHESTRATOR_SPLIT=0 -> flat single agent, no rebuild."]
```

**Why it pays on this project.** The catalog is part of the byte-stable prompt prefix, so moving
the plugin tools behind a subagent takes them out of the turn's prefix: fewer tokens **and** a
smaller KV-cache re-fill per turn — the resource that hurts on a home machine. The split is
**behavioral and testable** in the e2e tests
[`AgentBridge/e2e/AgentTools.Tests/Program.cs`](../e2e/AgentTools.Tests/Program.cs):

- an **all-native** set (`FileTool`, `WebTool`, `GitTool`) -> the orchestrator keeps everything,
  **no subagent** ("nothing to delegate");
- a **mixed** set with a loaded plugin -> the orchestrator keeps only the system tools, the plugin
  leaves; the subagent receives the **whole** set (self-sufficient, keeping the system tools too);
- a **pure-dispatcher** set (a plugin with no system tool) -> not split, it respects the client's
  explicit choice (`tools` override of the preset);
- a **kill-switch** `AGENTBRIDGE_ORCHESTRATOR_SPLIT=0|false|off|no` -> flat single agent, no
  rebuild.

> The **paths outside AgentBridge** (AIOffice panels, the
> `TaskSchedulerTool` scheduled chats) run **flat**: the split is host policy, not a library rule.
> A single entry point, `AgentTools.ExecuteSplit(harness, …)`, is what every textual chat path in
> this host calls. The voice path passes the two sets to `VoiceConversation` explicitly instead:
> it owns the streaming loop.
