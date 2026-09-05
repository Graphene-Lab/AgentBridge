# Puppet Mode Guide for AgentBridge TUI Testing
## Version 2.1 - 2026-08-24 (complete self-diagnosis procedure)

---

## Overview

Puppet Mode lets an agent (LLM or script) test the AgentBridge TUI automatically without manual interaction. The tester drives the TUI through a **TCP socket on localhost:5292**: it captures the screen as text, injects keys/mouse and types text. Each command is one JSON object sent on a connection and closed at EOF; the response is the resulting JSON/ASCII.

**Active ONLY in DEBUG builds** (`#if DEBUG`): the release build has no listener, no puppet surface, no PrintScreen binding. Never exposed to end users.

---

## Self-Diagnosis Procedure (for the LLM agent)

This is the procedure to follow to diagnose and fix the TUI with Puppet Mode. It is not an optional checklist: **an agent that skips it does not find the bugs** — state defects do not show up in a single capture; they need the change→close→reopen cycle and the propagation-log check.

### Complete test cycle (how it runs)

1. **Inspection** — read `Tui.cs` and list EVERY interactive element from the code: menus + menu items, slash commands (`Commands` array), dialogs and their controls (buttons, fields, checkboxes, dropdowns, lists, tabs), pages, palettes, the chat input/history, status bar, spinner, banner. Build the checklist from THIS list — an element present in the code but missing from the checklist is a failure of the inspection.
2. **Checklist** — one row per element with the 5 checks to complete (0 state-at-open, 1 visual, 2 interaction, 3 persistence, 4 propagation).
3. **Cycle** — for each element run checks 0→4 in order; capture BEFORE and AFTER each action; read the log after each action (check 4); when a check fails go to Phase 2bis (fix + full retest of that element).
4. **Report** — mandatory full log read, post-fix outcomes only, final state verification, then write the report (Phase 3).

The cycle is a single pass over the whole checklist: every menu, every command, every dialog control, every palette, the chat flow and the global keys are exercised at least once, and the log is read at the end to prove each interaction propagated.

### Interaction depth — opening is NOT testing

A test is not complete when the element merely opens. **Every element must be interacted with concretely, through its full depth**:

- A **menu item** → activate it (Enter) and verify the resulting command/panel/dialog.
- A **panel/dialog** → do not just look at it: click its buttons, change its fields, select from its lists.
- A **button** → press it and verify the effect (not just that it has focus).
- A **field/selector/checkbox** → change its value and verify the result.
- A **file/attachment flow** → add a file, select it, attach it, verify it appears attached, then clean up.

**Opening a dialog and closing it is NOT an interaction test.** An element that was only opened must be marked as NOT TESTED, not as passed.

### Fake-data testing (when real data is unavailable)

Many elements need data to be exercised (files, SIP/Telegram settings, providers). If real data is unavailable, **use fake data, then restore**:

1. Create fake data (e.g. `Sip:ListenPort 1111` via `/sip config`, or `add` a dummy file).
2. Interact with the element using that data (verify the setting is applied, the file appears in the list and can be selected/attached).
3. Verify via log that the interaction propagated (check 4).
4. **Restore** the initial/default state (remove the dummy file, revert the config, un-toggle features) and verify the restore in a capture.

This applies to EVERY element that accepts data: settings, configs, uploads, filters, prompts. Without fake-data exercise, the element is not tested.

### Phase 0 — Preparation
1. Read `Tui.cs` in full (`Show*`, `Run*`, `OnInputKeyDown` methods, menu bar, `/` commands).
2. Identify **every** interactive element from the code: menus, menu items, buttons (`Button`), text fields (`TextField`), selectors (`DropDownList`), lists (`ListView`), checkboxes (`CheckBox`), tabs, pages (`ShowPage`), palettes (`/`, `@`, `?`).
3. Identify **how events propagate**: menu items → `RunCommandByName` → `RunCommandAsync` → `cmd.Run`; buttons → `Accepted` handlers; pages → `ShowPage`; dialogs → `_app.Run(dlg)`.
4. Make sure the propagation points have a **LogStep** (command invoked/completed, dialog opened/closed, button pressed, value saved). Add them if missing: without logging you cannot prove a UI action reached the backend.
5. Prepare the **element todo list** (Phase 2): each element enters the list BOTH as "verify visually" and as "verify functionally" — a badly written element can escape the eye but not a checklist derived from the code.

### Phase 1 — Structural review (before any test)
- **Redundancies**: elements duplicating the same purpose (e.g. two dialogs for the same setting) → consider unifying them or marking them clearly.
- **Simplifications**: cryptic labels/items or overly long paths → improve them.
- **Menu placement**: every item must live in the correct top menu (Chat/File/Settings/Session/Web/Help).
- **Visual redundancy**: if two items show the same information with different values, that is a state bug (not a design choice).

### Phase 2 — Per-element verification (mandatory checks)

For EVERY element on the todo list, in order:

| # | Check | How | Pass criterion |
|---|-------|-----|----------------|
| 0 | **Initial state at open** | Open the element and capture BEFORE interacting | The element shows the **actual current setting** (active provider, checked checkbox, saved value) — not a default, an empty field or a placeholder value. This is the check that exposes state bugs (e.g. a dropdown never initialized). If missing → **state bug** (fix: initialize from backend/session, not from a default) |
| 1 | **Visual inspection** | Capture (`{"type":"capture"}`) before and after opening | Intuitive representation; no overlapped, mispositioned, truncated or **misaligned** elements (rows of the same list must start at the same column); understandable labels. **Buttons in a row must not overlap or even touch**: with auto-width buttons a fixed X layout breaks when the localized label grows longer than the gap (seen live: "Consenti utente…" drawn over "Blocca utente…" in the Italian Telegram panel; "Hinzufügen…" over "Bearbeiten…" in the German Model Setup). Any truncated button text (`Consenti uten`) is a red flag for overlap. If not intuitive → **redraw the GUI** (buttons should be laid out sequentially with `Pos.Right(prev) + 1`, never with guessed fixed X). |
| 2 | **Interaction inspection** | Use the element via puppet (keys/text/mouse) | The element responds: menu opens, checkbox toggles, dropdown changes, button activates |
| 3 | **State persistence (close/reopen)** | ① capture initial state → ② change the setting → ③ close (`escape`) → ④ reopen → ⑤ capture and COMPARE | The value at reopen matches the one you set. **If it does not persist**: BEFORE judging, read the log to distinguish: (a) **backend rejection** (e.g. "provider refused", missing API key) → the GUI showing the real state is CORRECT behaviour, not a bug; (b) **UI bug** (backend saved but the GUI shows the old value) → fix the UI; (c) **backend bug** (it does not save) → fix the backend |
| 4 | **Correct propagation (UI → backend)** | After the action, read `logs/<pid>.txt` | The expected sequence exists: `TUI running command: X` → `TUI command completed: X`; dialogs: `opened` → action (`saved`, `toggled`, `applied`) → `closed`; chat: `submit` → `spinner started` → `chat finished` → `spinner stopped`. **The sequence alone is not enough**: also verify the VALUE of the effect (updated status bar, confirmation note "provider now: X", state GET returning the new value) |

**Golden rules:**
- **Check 0 is the most important**: an element that does not show the real state at open is a bug even if everything else works.
- **State restoration**: every test that changes state (provider, AutoUpdate, checkboxes, settings) MUST restore the original state before moving to the next element (e.g. switch provider → switch back to the initial provider; verify with a capture that the status bar is back).
- **Reverse verification**: after the persistence test, verify that ALL displayed values match the ones you set (no field left at the previous value).
- **Search fields/selectors**: must be tested (filter, Tab-completion, selection) and must be on the checklist — visual inspection alone is not enough.
- **ReadOnly `DropDownList` selectors**: with puppet the arrow keys do NOT change the selection (you must open the list with click/Space and pick an item, or select the item with Enter after highlighting it). A dropdown that does not change with arrows is NOT a bug — it is the control's behaviour. Document the method used in the report.
- **Alignment**: in lists with markers (bullet, checkmark) the unmarked rows must carry the same leading fill (spaces) so all texts start at the same column.
- ALWAYS close open dialogs (`escape`) before the next element.
- Pause ≥ 300–500 ms between commands (the pump runs at 250 ms).
- For menus: open with F10, navigate with arrows, activate with Enter, close with Esc.
- **Operator input**: if the user is typing in the TUI while the test runs, injected input concatenates to the existing text and may go out as a chat message. Before every `/...` command make sure the input is empty (Esc clears it).

### Phase 2bis — Fix and retest

When a check fails:
1. **Fix** the problem (UI or backend, depending on the check 3/4 diagnosis).
2. **Repeat the test** on the fixed element (ALL checks 0–4), not just the failed one.
3. Record in the report: symptom → cause → fix → post-fix verification.

### Phase 3 — Final report

**BEFORE writing the report it is MANDATORY to:**
1. **Inspect the full log file** (`logs/<pid>.txt`) from the beginning to the end of the test: every action performed must have its propagation sequence; a UI action with no log entry = missing propagation (bug). Do not close the test without this read.
2. **Verify that everything that failed has been fixed and retested** (Phase 2bis): the report checks of fixed elements must reflect the POST-FIX outcome, not the pre-fix one.
3. **Verify the app's final state**: no dialog left open, state restored (initial provider, checkboxes as at the beginning).

Write the report with this structure (in `docs-dev/TUI-TEST-REPORT.md`):

```
## ELEMENTS IDENTIFIED FROM CODE ANALYSIS (Tui.cs)
(the complete list derived from the source — menus, dialogs, fields, palettes, ...)

## PER-ELEMENT OUTCOME
| Element | State at open | Visual | Interaction | Persistence | Propagation (log) | Notes |
|---------|---------------|--------|-------------|-------------|-------------------|-------|

## OVERALL LOG REVIEW
(full log read: does every action have its propagation sequence? yes/no)

## BUGS FOUND AND FIXED
- symptom (as the user sees it) · cause (from code analysis) · fix · post-fix verification

## SUGGESTED IMPROVEMENTS (non-blocking)
- redundancies, simplifications, placements
```

---

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                    AgentBridge (DEBUG build)                 │
│  ┌───────────────────────────────────────────────────────┐  │
│  │              Terminal.Gui Application                  │  │
│  │  ┌─────────────────────────────────────────────────┐  │  │
│  │  │        Main loop (UI thread)                     │  │  │
│  │  │   Puppet pump (250 ms recurring timer):          │  │  │
│  │  │   • drains the injection queue                   │  │  │
│  │  │   • refreshes the screen snapshot                │  │  │
│  │  └─────────────────────────────────────────────────┘  │  │
│  └───────────────────────────────────────────────────────┘  │
│                              ▲ enqueue / snapshot            │
│  ┌───────────────────────────────────────────────────────┐  │
│  │             PuppetMode (static, Tui.cs)               │  │
│  │  • ANSI_Tui_Capture() → snapshot (never blocking)     │  │
│  │  • InjectKey / InjectText / InjectMouse → queue       │  │
│  └───────────────────────────────────────────────────────┘  │
│                              ▲ TCP                          │
│  ┌───────────────────────────────────────────────────────┐  │
│  │     TCP listener localhost:5292 (Program.cs, DEBUG)   │  │
│  │     one JSON per connection, response on close        │  │
│  └───────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
                              ▲
                              │ TCP localhost:5292
                              │
                    ┌─────────────────┐
                    │  Test Runner    │
                    │  (Agent/Script) │
                    └─────────────────┘
```

---

## TCP Protocol (localhost:5292)

One connection = one command:

1. Open the socket on `127.0.0.1:5292`.
2. Send the JSON body (UTF-8), then **shutdown the send side** (`Socket.Shutdown(Send)`) to signal EOF: the server reads until EOF, executes, writes the response, then closes.
3. Read the response until EOF.

### Commands

| type      | Fields                               | Response |
|-----------|--------------------------------------|----------|
| `capture` | optional `"grid":true`               | ASCII text of the whole TUI screen (`text/plain` in the body); with `grid` a coordinate ruler is prepended (column tens+units header rows and a `0000 ` row prefix) so mouse x/y can be read exactly from the capture |
| `hit`     | `x`, `y` (int, terminal cells)       | The views stacked under the point, deepest last (e.g. `0: Dialog "Telegram" ...` `1: Button "Ricarica configurazione" {X=47,Y=10,Width=28,Height=2}`) — use it BEFORE a mouse click to confirm which control will receive it |
| `key`     | `key` (key name, see table)          | `{"ok":true}` or `{"error":"…"}` |
| `text`    | `text` (string)                      | `{"ok":true}` |
| `mouse`   | `x`, `y` (int, terminal cells), `flags` (flag name) | `{"ok":true}` |

> **Principle**: the puppet injects only what a real user can do — keys, text and mouse.
> There are NO command-specific shortcuts (no `tab`, no `enter`, no `f4`): if a UI action
> cannot be reproduced with these three primitives, that is a TUI bug (the user cannot do
> it either), not a puppet gap. Fix the UI, then test it with the generic primitives.

Examples:

```bash
# Capture screen with coordinate grid (read exact cell coordinates for mouse clicks)
powershell -File tools\puppet.ps1 '{"type":"capture","grid":true}'

# Hit-test: which views are under cell (77,16)?
powershell -File tools\puppet.ps1 '{"type":"hit","x":77,"y":16}'

# Capture screen
powershell -File tools\puppet.ps1 '{"type":"capture"}'

# Press F10 (opens the Chat menu)
powershell -File tools\puppet.ps1 '{"type":"key","key":"f10"}'

# Type text into the input
powershell -File tools\puppet.ps1 '{"type":"text","text":"ciao mondo"}'

# Left click at coordinates (50,10)
powershell -File tools\puppet.ps1 '{"type":"mouse","x":50,"y":10,"flags":"LeftButtonClicked"}'

# Switch the Model Setup dialog to the next page (Ctrl+PageDown — a real user key)
powershell -File tools\puppet.ps1 '{"type":"key","key":"Ctrl+PageDown"}'
```

> `tools/puppet.ps1` is the reference client (PowerShell). For the RAW protocol in another language see "Example Scripts". Commands containing Windows paths or special characters must be sent from a JSON file via `tools/puppet-body.ps1 <file>` (backslashes escaped as `\\`).

### Mouse click calibration (learned from testing)

- **The capture grid is authoritative**: cell (x, y) in the capture = `{"type":"mouse","x":x,"y":y}`. The grid rows are the 0-based screen rows.
- **Buttons are 2 rows tall** (text row + shadow row): a click on the SHADOW row does not activate the button. Hit-test first, then click the TEXT row.
- **Buttons added with `dlg.AddButton` (dialog footer)** respond to the injected `LeftButtonClicked`. **Buttons added with `dlg.Add(...)` inside a dialog's content** may not trigger `Accepted` from a synthetic click (Terminal.Gui routing); prefer Tab-navigation to focus them + Enter, or the equivalent slash command.
- **Tabs (v2.4.17) headers are NOT mouse-clickable** (the `Tabs` control has no mouse handler for headers) and the tab pages are not reachable by focus traversal (Tab cycles only inside the current page). The TUI therefore binds **Ctrl+PageDown / Ctrl+PageUp** (the framework's documented TabGroup keys) to switch pages — a real user key, reproducible with `{"type":"key","key":"Ctrl+PageDown"}`. The dialog shows this hint under the pages.

### Supported key names

`enter` · `escape`/`esc` · `tab` · `backspace` · `delete`/`del` · `space` · `printscreen` · `cursorup`/`up` · `cursordown`/`down` · `cursorleft`/`left` · `cursorright`/`right` · `pageup`/`pgup` · `pagedown`/`pgdn` · `home` · `end` · `f1`…`f12`.

Any other name falls through to `Key.TryParse` (standard forms "Ctrl+C", "Alt+X", "Shift+A", "Ctrl+Alt+Shift+X", "120" for unicode).

### Mouse flags

`LeftButtonPressed` · `LeftButtonReleased` · `LeftButtonClicked` · `LeftButtonDoubleClicked` · `LeftButtonTripleClicked` · `MiddleButton*` · `RightButton*` · `Button4*` · `WheeledUp` · `WheeledDown` · `WheeledLeft` · `WheeledRight` · `Shift` · `Ctrl` · `Alt` · `PositionReport` · `AllEvents`

---

## Why the pump and not `Application.Invoke`?

**NEVER call `Application.Invoke`/`TimedEvents.Add` from background threads in the puppet path.** In Terminal.Gui v2.4.17 `TimedEvents.RunTimers()` holds the `_timeoutsLockToken` lock while executing callbacks; a modal dialog opened by an injected key runs its nested RunLoop INSIDE that callback → the lock stays held for the whole dialog lifetime → every `Invoke`/`Add` from a background thread blocks forever in `Monitor.Enter_Slowpath` (deadlock observed and diagnosed with `dotnet-stack`).

The implemented solution:

- The TCP handlers **enqueue** actions (`ConcurrentQueue<Action>`), they do not execute them.
- A **recurring timer (250 ms) registered on the UI thread** (`PuppetMode.StartPump()`, called from the TUI constructor) drains the queue and refreshes the snapshot.
- The timer is **re-armed BEFORE draining**: if an injection opens a modal dialog and blocks the callback, the timer keeps firing inside the nested loop (TimedEvents is re-entrant on the same thread) → injections and snapshot stay alive with the dialog open.
- `ANSI_Tui_Capture()` returns the **cached snapshot** (at most ~250 ms stale): a string read, never blocking.

Practical consequence: wait ≥ 300–500 ms between commands (the pump runs in 250 ms steps).

---

## Integrated PrintScreen capture

In DEBUG builds, the **PrintScreen** key inside the TUI saves the snapshot to `tui-screenshots/puppet-<timestamp>.txt` next to the executable, shows the path in the status bar and writes a `LogStep`. It also works injected via TCP (`{"type":"key","key":"printscreen"}`). The testing agent can then ask the operator (or itself via TCP) to press PrintScreen and read the file.

---

## Diagnosis with the log file

Start the app with `--enable-log` (or set `AIOrchestrator.Log.IsEnabled = true`) to write `logs/<pid>.txt`. Key points log with `LogStep`:

- `[Puppet]` — listener started, command received, response, errors.
- `[Pump]` / `[StartPump]` — pump started, injections executed.
- `[RunCommandByName]` / `[RunCommandAsync]` — commands invoked by menu items / slash commands, completed or failed: **proof that the TUI interaction really executed the underlying action** (not just the visual effect).
- `[ShowModelSetupDialog]` — dialog opened/closed, saved, Add/Edit/Remove buttons.
- `[TUI submit]` — chat message sent (prompt and result).
- `[OpenIssues]` — issues page opened.
- `[PuppetCapture]` — PrintScreen → file saved.

Recommended verification flow: run a puppet sequence, then read the log and compare the executed TUI commands with the captures.

---

## Typical Test Flow

```powershell
# 1. Launch the DEBUG build with logging (from the developer):
#    bin\Debug\net10.0\agent.exe --enable-log

# 2. Capture initial state
powershell -File tools\puppet.ps1 '{"type":"capture"}'

# 3. Open the Chat menu (F10) and check it contains "Esci"
powershell -File tools\puppet.ps1 '{"type":"key","key":"f10"}'
Start-Sleep -Milliseconds 500
powershell -File tools\puppet.ps1 '{"type":"capture"}'

# 4. Close with Esc
powershell -File tools\puppet.ps1 '{"type":"key","key":"escape"}'

# 5. Open "Impostazioni principali" (menu Impostazioni/Settings → 1st item) and capture the dialog
powershell -File tools\puppet.ps1 '{"type":"key","key":"f10"}'
Start-Sleep -Milliseconds 500
powershell -File tools\puppet.ps1 '{"type":"key","key":"cursorright"}'
Start-Sleep -Milliseconds 500
powershell -File tools\puppet.ps1 '{"type":"key","key":"cursorright"}'
Start-Sleep -Milliseconds 500
powershell -File tools\puppet.ps1 '{"type":"key","key":"enter"}'
Start-Sleep -Seconds 2
powershell -File tools\puppet.ps1 '{"type":"capture"}'   # → dialog with the active/default-model label

# 6. Close the dialog
powershell -File tools\puppet.ps1 '{"type":"key","key":"escape"}'

# 7. Type text and send it
powershell -File tools\puppet.ps1 '{"type":"text","text":"ciao mondo"}'
Start-Sleep -Milliseconds 500
powershell -File tools\puppet.ps1 '{"type":"capture"}'

# 8. Verify the interactions in the log
Get-Content logs\<pid>.txt -Encoding UTF8 | Select-String -Pattern 'TUI|Puppet'
```

---

## Example Scripts (Python)

```python
import json, socket, time

def puppet(cmd: dict) -> str:
    s = socket.create_connection(("127.0.0.1", 5292), timeout=10)
    s.sendall(json.dumps(cmd).encode("utf-8"))
    s.shutdown(socket.SHUT_WR)              # EOF: the server executes and responds
    data = b""
    while True:
        chunk = s.recv(65536)
        if not chunk:
            break
        data += chunk
    s.close()
    return data.decode("utf-8")

def capture() -> str:
    return puppet({"type": "capture"})

def key(name: str):
    return puppet({"type": "key", "key": name})

def text(t: str):
    return puppet({"type": "text", "text": t})

def mouse(x: int, y: int, flags: str = "LeftButtonClicked"):
    return puppet({"type": "mouse", "x": x, "y": y, "flags": flags})

print(capture())          # initial state
key("f10")                # Chat menu
time.sleep(0.5)
print(capture())          # menu open (check "Esci")
key("escape")             # close
```

---

## Troubleshooting

| Problem | Cause / Solution |
|---|---|
| `Connection refused` on 5292 | RELEASE build (no listener) or app not started. Use the DEBUG build: `bin\Debug\net10.0\agent.exe`. |
| Response timeout | The server executes the command but waits for EOF: you must shut down the send side (`shutdown(SHUT_WR)`). |
| Capture shows a stale screen | Snapshot refreshed every 250 ms: wait ≥ 300 ms after the action. |
| Injections have no effect with a dialog open | Check the pump is running: look for `[Pump] executed` in the log (if there was a regression to the Invoke design, re-read the "Why the pump" section). |
| `A binding for Enter exists ([Accept], Key=Enter)` in the log | **Do NOT add `list.KeyBindings.Add(Key.Enter, Command.Accept)` to a `ListView`**: in v2.4.17 the ListView already binds Enter → `Command.Accept` by default, and re-adding it throws, breaking the dialog mid-construction. `Accepted` fires through the existing binding once the list has focus. |
| The Model Setup dialog tab won't switch | Tab headers have no mouse handler in v2.4.17, so use the real user key: `{"type":"key","key":"Ctrl+PageDown"}` (next) / `Ctrl+PageUp` (previous). If even that fails, the TUI binding is broken — fix it in `ShowModelSetupDialog` (Tui.cs), don't add a puppet workaround. |
| Box characters unreadable in the log | Read the file with `-Encoding UTF8` (PowerShell) — the file is UTF-8. |
| I want to see whether a TUI action really executed | Look at `logs/<pid>.txt`: `[RunCommandByName]`, `[ShowModelSetupDialog]`, `[TUI submit]`, etc. |

---

## References

- Source code: `Tui.cs` (class `PuppetMode`, `ConsoleTui.Tui.PuppetCapture`), `Program.cs` (listener + TCP handlers).
- Reference client: `tools/puppet.ps1`.
- Diagnosis: `dotnet-stack report -p <pid>` for thread stacks.
- Terminal.Gui v2: `docs-dev/TUI-DEVELOPMENT.md` (offline local guide).
