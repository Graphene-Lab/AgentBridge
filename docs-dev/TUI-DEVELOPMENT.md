# Terminal.Gui v2 — local developer guide (AgentBridge TUI)

This is the **local reference for every change to `Tui.cs`** (and any other
Terminal.Gui code in this repo). It distils the official API documentation of
the exact package versions this project pins, plus the lessons learned from
real bugs. **Consult it before editing the TUI; you should not need the web.**

The full official API docs ship with the NuGet packages and live on this
machine:

| Package | Version | Local XML docs (official, shipped with the package) |
|---|---|---|
| `Terminal.Gui` | 2.4.17 | `C:\Users\andre\.nuget\packages\terminal.gui\2.4.17\lib\net10.0\Terminal.Gui.xml` |
| `Terminal.Gui.Editor` | 2.5.7 | `C:\Users\andre\.nuget\packages\terminal.gui.editor\2.5.7\lib\net10.0\Terminal.Gui.Editor.xml` |

Other useful local sources: the user guide `docs/TUI.md`, the ConPTY smoke
harness `e2e/TuiSmoke/Program.cs`, and the real usage in `Tui.cs` itself.

> **Cross-platform rule.** The TUI must run on **Windows, Linux and macOS**.
> Never write Windows-only code without a `OperatingSystem.IsWindows()`
> guard and a non-Windows fallback (see [Cross-platform](#9-cross-platform-windows-linux-macos)).

---

## 1. Versions & packages

- **`Terminal.Gui` 2.4.17** is pinned, and **`Terminal.Gui.Editor` 2.5.7**
  depends on `Terminal.Gui >= 2.4.17` (verified in its nuspec) — the effective
  Terminal.Gui version is therefore **2.4.17**. Keep the pair in sync when
  upgrading.
- **`TextView` is obsolete** in v2. The text editing/display control is the
  **`Editor`** from the separate package `Terminal.Gui.Editor` (repo
  `tui-cs/Editor`, adapted from AvaloniaEdit): rope-backed document, caret,
  undo, find/replace, folding, optional TextMate highlighting.
- **API style: instance-based.** Create the app with
  `Application.Create().Init()` (do **not** use the legacy static
  `Application.Init()` singleton path); hold the `IApplication` reference and
  drive everything through it. This repo uses `Terminal.Gui.App`,
  `Terminal.Gui.Views`, `Terminal.Gui.ViewBase`, `Terminal.Gui.Input`,
  `Terminal.Gui.Drawing`, `Terminal.Gui.Configuration` namespaces (see the
  `using` block at the top of `Tui.cs`).
- `Terminal.Gui.Drawing.Attribute` collides with `System.Attribute` — the
  repo aliases it: `using TuiAttribute = Terminal.Gui.Drawing.Attribute;`.

## 2. Application model

```csharp
var app = Application.Create().Init();          // one per process
var window = new Window { ... };
window.Initialized += (_, _) => someView.SetFocus();  // see focus notes
app.AddTimeout(TimeSpan.FromMilliseconds(60), () => { /* one-shot */ return false; });
app.AddTimeout(TimeSpan.FromMilliseconds(100), () => { /* recurring */ return true; });
app.Run(window);                                // blocks until RequestStop
app.Dispose();
```

Key members (`Terminal.Gui.App.Application`, documented in the local XML):

| Member | Notes |
|---|---|
| `Create()` / `Init()` | Create + initialise the application + driver. |
| `Run(IRunnable)` | Run a view (window or modal dialog) to completion. Nested calls work (dialogs). |
| `RequestStop(IRunnable)` | Stop the given runnable; the `Run` call returns. This is how dialogs and the main window close. |
| `AddTimeout(TimeSpan, Func<bool>)` | Main-loop timer. Return `true` to repeat, `false` for one-shot. Also used to defer work to the next iteration. |
| `RemoveTimeout(object)` | Cancel a timer. |
| `Invoke(Action)` | Run an action on the main loop. **See the Invoke semantics box below.** |
| `TopRunnableView` | The view currently on top of the session stack (e.g. the running dialog). |

### `Invoke` semantics (a real bug was caused by this)

`Application.Invoke` is **not fire-and-forget** in 2.4.17:

- called **from the main UI thread** → executes the action **synchronously**;
- called **from a background thread** → queues it and runs it on the next
  main-loop iteration.

Consequence: inside a `DocumentChanged`/`ContentChanged` callback (which runs
on the UI thread), `Invoke` runs your code *immediately*, in the middle of the
document change — mutating the document there throws
**"Cannot change document within another document change"**. Fix used here:
intercept the trigger key in `KeyDown` (before insertion) and consume it, or
defer with `AddTimeout(TimeSpan.Zero, ...)` (never synchronous).

**Marshalling rule.** Terminal.Gui mutates views only on the main loop thread.
Every UI touch from a background task (HTTP, streaming) must go through
`Ui(action) => app.Invoke(action)`. `Ui()` must also drop actions after
dispose. Collections shared with background tasks need their own lock (see
`_stateLock` in `Tui.cs`).

## 3. Layout & coordinates

- Every view has `X, Y, Width, Height` taking `Pos` / `Dim` objects:
  `Dim.Fill()`, `Dim.Percent(n)`, `Pos.Right(view)`, `Pos.Bottom(view)`,
  `Pos.AnchorEnd(1)` (1 row up from the bottom), `Dim.Auto(...)`.
- **Viewport vs content:** a view has a *viewport* (visible area) and a
  *content size* (what is scrollable). `Viewport` is a property; content
  exceeds viewport for scrollable views (ListView, Editor).
- **`CanFocus` defaults to `false`** — and a plain container `View` with
  `CanFocus=false` **blocks focus for every focusable child below it**
  (`SetFocus` returns false, keys never reach the input). Custom containers
  (`contentArea`, `inputArea` in `Tui.cs`) must set `CanFocus = true`.
  `FrameView` is focusable by default; `Label` is not.
- Sizing an `Editor`'s height to its wrapped content: the wrap column is the
  viewport width and **`Editor.GetWrapMap`/`GetWrapColumn` are private in the
  real DLL** (the XML docs wrongly list them as public). Estimate rows with
  `ceil(lineLength / viewportWidth)` per line (see
  `EstimateWrapRows` in `Tui.cs`).

## 4. Theming

- A **`Scheme`** is a set of `Attribute`s (`Normal`, `Focus`, `HotNormal`,
  `HotFocus`, ...) mapping to `Color`/`TuiAttribute` foreground+background.
- Schemes are registered by name with
  `SchemeManager.AddScheme(name, scheme)` and assigned to views via
  `SchemeName` (there is **no `View.Scheme` property** in 2.4.17).
- ⚠️ `AddScheme("Dark", ...)` **overrides the built-in "Dark" scheme
  globally** — every standard view (buttons, dialogs, MessageBox, text
  fields) then uses your colours. That is what `Tui.cs` does on purpose;
  if you only want a per-view look, register a uniquely named scheme instead.
- Built-in scheme names exist (see `SchemeManager` in the XML docs) and the
  driver/theme system applies them; views without an explicit `SchemeName`
  inherit the application theme.
- **16 colors are the portable baseline** across terminals/OSes.
  `DriverSettings.Force16Colors` (Terminal.Gui.Configuration) forces 16-color
  mode; `DriverSettings.SizeDetection` controls how the console size is
  detected (relevant on Windows, see §8). The AGENT logo gradient in `Tui.cs`
  uses `BrightBlue → BrightMagenta → BrightRed` — all within the 16-color set.

## 5. Keyboard & focus

- **`Key` is a class** (`Terminal.Gui.Input`), not an enum: `Key.CursorUp`,
  `Key.Enter`, `Key.Esc`, `Key.F1`, `Key.C.WithCtrl`, `Key.Enter.WithShift`.
  `WithCtrl`/`WithShift`/`WithAlt` are properties returning a new `Key`.
  There is **no `Key.Slash`** — use `Key.Empty` or `(Key)'/'` (implicit
  conversion from `char` works).
- **Events (v2):** `KeyDown` (`EventHandler<Key>`, set `key.Handled = true`
  to consume), `KeyDownNotHandled` (bubbles up from children — the place to
  swallow keys the focused editor left unhandled), `HasFocusChanged`
  (`e.NewValue`), `ValueChanged`, `Accepted`, `MouseEvent`, `ContentChanged`.
  There are no v1-style `KeyPress`/`Enter`/`Leave`/`Changed`/`Clicked`.
- **`KeyDown` fires before the view processes the key** — intercepting
  `/`, `@`, `?` there keeps the trigger character out of the document
  (prevents the DocumentChanged crash, see §2).
- **Arrow-key focus traversal is global.** At `Application` level, unhandled
  arrow keys are aliases of `NextTabStop`/`PreviousTabStop`
  (`ApplicationNavigation`) — so an arrow at a text boundary *moves focus to
  the next view* instead of doing nothing. The `Editor` consumes arrows while
  moving within the text; when it reaches a boundary the key bubbles up.
  Fix used here: on the input's parent, `KeyDownNotHandled` swallows
  `CursorLeft/Right/Up/Down` (plain and `WithCtrl`), `Home/End`,
  `PageUp/PageDown`.
- **Initial focus:** the framework puts the initial focus on the first
  focusable child (the `MenuBar`). Re-assert focus with
  `window.Initialized += ... SetFocus()` **plus** a ~60 ms one-shot
  `AddTimeout` that re-runs `SetFocus()` — `Initialized` alone is not enough
  in all cases.
- **Window's default `Esc → Quit`:** a `Window` binds Esc to quit by default.
  If a stray Esc must never close the app, handle it in the window's
  `KeyDown` and mark `Handled` (as `Tui.cs` does), or bind the window to
  another command.

## 6. Mouse

- `View.MouseEvent` receives `Mouse` with `Flags` (`MouseFlags.WheeledUp`,
  `WheeledDown`, `Clicked`, ...) and position. The chat auto-follow in
  `Tui.cs` toggles on wheel events (`WheeledUp` → stop following,
  `WheeledDown` → resume).
- `ListView` supports click selection, double-click accept, wheel scrolling
  out of the box. `CheckBox` toggles on click and accepts on double-click.
- Mouse support is enabled by the driver when the terminal reports it
  (xterm mouse tracking on Unix, Win32 input records on Windows).

## 7. Core views

All in `Terminal.Gui.Views` unless noted. Each entry lists the members this
repo actually uses (they are the verified, working subset).

### Window / FrameView
- `Window`: top-level container, has `Title`, default `Esc→Quit` binding.
- `FrameView`: titled bordered container, focusable by default (used for the
  AGENT logo panel and the chat panel).

### MenuBar / MenuItem
- `MenuBar(IEnumerable<MenuBarItem>)`; `MenuBarItem(title, MenuItem[])`.
- `MenuItem(string title, Key key, Action action)` — `Key.Empty` when no
  shortcut. Menu items route to the same command path as slash commands
  (`RunCommandByName`).
- **v2 has no checkmark state on menu items.** A toggle's state must be shown
  in the title text (as `Auto-Update: on/off` does in `Tui.cs`).
- `MenuItem` extends `Shortcut` (command text + help text + key view + action)
  and supports `SubMenu` for cascading menus.

### StatusBar (the TUI uses it for the key hints)
- `StatusBar(IEnumerable<Shortcut>)` — snaps to the bottom of the viewport,
  shows command/help/key triples. The docs recommend a **context-sensitive**
  instance per UI context. `Tui.cs` uses a display-only `Shortcut` (no `Key`)
  for the static key-hint bar; the dynamic state line above it is a `Label`
  (comprehensible segments — no raw ids or cryptic abbreviations).
- `Shortcut` is a composite of `CommandView` (command text + hotkey),
  `HelpView` (help), `KeyView` (key binding); set `Action` and/or `Key`.
  `Bar` is the base class arranging `Shortcut`s horizontally/vertically.

### Dialog / Dialog\<T\> / MessageBox
- `Dialog` is centered with auto sizing by default; `AddButton(Button)` adds
  buttons along the bottom; Esc cancels (closes) it. Buttons are `Button`
  views with `Accepted` events; `IsDefault = true` makes Enter activate.
- `Dialog<T>` is the typed variant (result value). `MessageBox.Query(app, ...)`
  for simple prompts (it runs its own modal loop).
- **Modal pattern:** `app.Run(dlg)` blocks until `app.RequestStop(dlg)`; then
  `dlg.Dispose()` and re-focus the input. Return a result through a captured
  local (the picker helpers in `Tui.cs` do exactly this).
- ⚠️ **Do not open a dialog from inside an Editor `DocumentChanged` callback**
  (see §2) — the modal loop re-enters the main loop and the deferred document
  mutation throws.

### ListView (single- and multi-select)
- `Source` (an `IListDataSource`; the default `ListWrapper<T>` wraps an
  `ObservableCollection<T>` and renders `ToString()`), `SelectedItem`,
  `Accepted` event (Enter/double-click), `KeystrokeNavigator` (type-to-search).
- **Multi-select / checklist is built in:** set `ShowMarks = true` and the
  `IListDataSource` manages marks via `IsMarked(int)` / `SetMark(int, bool)`.
  **SPACE toggles the mark** on the selected row; `MarkMultiple` switches the
  glyphs between **checkbox style and radio-button style** (see
  `RenderMark(listView, item, row, isMarked, markMultiple)`). Marks exist even
  with `ShowMarks=false` — the flag only controls the visible glyphs.
- This is the mechanism a future "tool selection" checklist should use
  (instead of the current single-choice picker).

### CheckBox
- `Value` is `CheckState` (`UnChecked`/`Checked`/`None`); `AllowCheckStateNone`
  (default false) enables the third state (⬛). `RadioStyle = true` renders
  radio glyphs (●). Events: `ValueChanging` (cancellable) / `ValueChanged`.

### TextField / DropDownList
- `TextField`: single-line text, `Secret = true` masks input (API keys,
  passwords). `Text`, `ValueChanged`.
- `DropDownList`: TextField + popover ListView (a combo box). Set `Source` to
  a `ListWrapper<T>`, `ReadOnly = true` for pick-only. `DropDownList<T>` is
  the typed variant (interchangeable with `OptionSelector<T>`).

### Tabs
- `Tabs` container: add tab pages (a `View` with a `Title` acts as a tab
  page). Used by the models/providers setup dialog.

### Editor (Terminal.Gui.Editor) — chat log + prompt input

The two Editor roles in `Tui.cs`:

- **Read-only chat log:** `ReadOnly = true`, `WordWrap = true`,
  `CanFocus = false` (it never steals focus; scroll via `ScrollVertical`).
  Assign text with `Document = new TextDocument(text)` or set
  `Document.Text`. Setting `CaretOffset` to the end scrolls the viewport
  (auto-follow). ⚠️ With `CanFocus = false` the user **cannot scroll it with
  the keyboard** — a page dialog that must scroll needs focus or explicit key
  handling (§8).
- **Multi-line prompt:** `Multiline = true`, `WordWrap = true`,
  `GutterOptions = GutterOptions.None`, auto-grow height via the wrap-row
  estimate (§3). The editor binds **plain Enter to inserting a newline** — the
  app intercepts plain Enter in `KeyDown` (fires before the editor's binding)
  to submit, and **Shift+Enter is NOT bound**, so a newline is inserted
  manually with `Document.Insert(caret, "\n"); CaretOffset = caret + 1`.
- Document API (verified): `TextDocument` (rope-backed) with `Text`,
  `TextLength`, `LineCount`, `Lines` (of `DocumentLine`: `Offset`, `Length`,
  `TotalLength`, `LineNumber`), `UndoStack`, `BeginUpdate()/EndUpdate()`
  (`IsInUpdate`). `Document.Insert(offset, text)`, `Document.Remove(...)`.
- Find/replace: `FindNext/FindPrevious/ReplaceNext/ReplaceAll` overloads.
- Mutating the document **inside a DocumentChanged callback throws** — see §2
  for the workaround.

## 8. Known pitfalls & bugs (verified in this repo)

| # | Pitfall | Symptom | Fix (as applied in `Tui.cs`) |
|---|---|---|---|
| 1 | Container `View` with `CanFocus=false` | Keys never reach the input; Esc closes the app | `CanFocus = true` on custom containers |
| 2 | `Application.Invoke` is synchronous on the UI thread | "Cannot change document within another document change" crash | Intercept `/` `@` `?` in `KeyDown`; defer clears with `AddTimeout(TimeSpan.Zero, ...)` |
| 3 | Unhandled arrows = focus traversal | Pressing an arrow at a text boundary moves focus out of the prompt | Swallow movement keys in the parent's `KeyDownNotHandled` |
| 4 | Initial focus lands on the MenuBar | Typing/Esc never reaches the input | `Initialized` + 60 ms one-shot `SetFocus` |
| 5 | Read-only page dialog with `CanFocus=false` Editor | **Arrow keys / PgUp/PgDn do nothing** (the "Telegram menu doesn't scroll" bug) | Give the scrollable view focus, or handle keys explicitly; use `ShowMarks`/ListView where selection matters |
| 6 | **Web-client launch dirties the console** | After `/web`, the screen fills with `^[[8;30;120t` spam | See box below |
| 7 | **`Dim.Fill()`-based heights collapse in stacked dialog layouts** | A ListView with `Height = Dim.Fill() - 7` inside a modal dialog rendered **1 row** (the Fill math resolved against the dialog's content box, went negative and clamped) | Use **fixed heights** for stacked lists in a dialog (preset list + tool list in `ShowToolsDialog`); `Dim.Fill() - n` is safe only for a single list against a tall dialog (picker pattern) |
| 8 | **Editing `Dictionary.*.resx` from the CLI does not regenerate `Dictionary.Designer.cs`** | New `Dictionary.X` members are missing → compile errors CS0117 | The VS custom tool only runs in the IDE; from the CLI run `scripts/regenerate-dictionary-designer.ps1` (replicates StronglyTypedResourceBuilder output, UTF-8 no BOM) |

### 6 — Web client launch: the `ESC[8;30;120t` console leak

Symptom: launching the web client (`/web`) makes the TUI screen fill with
repeated `30;120t^[[8;30;120t` — a **resize-text-area** ANSI sequence
(`CSI 8 ; rows ; cols t`).

Verified facts:

- `Terminal.Gui.dll` 2.4.17 **does not emit** that sequence as a literal
  (byte-scan of the assembly: no `ESC[8;` / `ESC[?` patterns).
- The driver **parses** it: the ANSI parser contains the
  `CSI_SetTerminalWindowSize` handler, and the Windows driver tracks window
  size state (`_lastWindowSizeBeforeMaximized`, `WindowBufferSizeRecord`).
- The launch code uses
  `Process.Start(cmd.exe, "/c start \"\" \"start.bat\" --provider …")` with
  **`UseShellExecute = false`** — the child **inherits the TUI's console and
  std handles**. Root cause hypothesis (consistent with all facts): the new
  launcher terminal (cmd → `start.bat` → `start /B powershell`) writes its
  output/resize sequences **into the caller's console** when it shares it or
  when VT processing is off on the shared output — the bytes then render as
  literal garbage inside the TUI's alternate screen buffer.

Fix direction (validate by reproduction during implementation):

1. **Fully detach the child from the TUI console**: redirect stdin/stdout/
   stderr (to a log file or NUL on Windows / `/dev/null` on Unix) and request
   a **new console** (`UseShellExecute = true` gives console apps their own
   window; or use `CreateProcess` with `CREATE_NEW_CONSOLE`).
2. Never let a child write into the TUI's console — including via `cmd` /
   `start /B`, which runs in the caller's console.
3. After launching, force a full redraw of the TUI if any garbage slipped
   through.
4. Verify with the ConPTY harness (§10): the captured screen after `/web`
   must contain no `ESC[8;` bytes.

Cross-platform: on Linux/macOS there is no console inheritance the same way,
but the same rule applies — redirect the child's std handles
(`/dev/null` or a log) so no child output can reach the TUI.

## 9. Cross-platform (Windows / Linux / macOS)

The TUI must work on all three. Rules that keep it portable:

- **Drivers.** Terminal.Gui picks the console driver per platform
  (Windows: Win32 console / VT; Unix: curses-style terminal handling; ANSI
  fallback). You normally do not touch drivers; stay on the documented
  `Application`/view API. `DriverSettings` (Terminal.Gui.Configuration) is
  the only driver-level knob you should need (`Force16Colors`,
  `SizeDetection`).
- **Colours.** Use the 16-color set for guaranteed visibility everywhere;
  Unicode glyphs (⣾ spinner, ●/○, ☑/☐) are fine on modern terminals — the
  library replaces wide glyphs that would be clipped (`GlyphSettings`).
- **Process launching.** Do not assume `cmd.exe` or `bash`:
  - Use `Process.Start` with `UseShellExecute` and platform-appropriate
    entries (`start.bat` on Windows, `start.sh` on Linux/macOS — the web
    client already ships both).
  - Always redirect or detach child std handles (§8).
- **Audio.** `winmm.dll` `PlaySound` is **Windows-only** — guard with
  `OperatingSystem.IsWindows()` and provide the non-Windows path (the TTS
  code in `Tui.cs` already does: playback note + saved file path).
- **Paths.** Never hardcode separators or Windows roots: use
  `Path.Combine`, `Environment.SpecialFolder`, `AppContext.BaseDirectory`
  (the server already resolves content root to the executable folder).
- **Newlines / encodings.** Normalise `\r\n` → `\n` when processing text
  (see `RefreshHistory` in `Tui.cs`); write files with explicit encodings.
- **Localization.** All UI strings come from the `Resources/Dictionary.*.resx`
  satellites (`Dictionary.*` in code), never hardcoded — including on
  Linux/macOS where the system culture decides the language. Command names
  (`/agent`, `/web`, ...) are never translated.

## 10. Verification workflow

1. **Build** the AgentBridge project (the TUI is compiled into `agent.exe`).
   The repo builds sibling tool plugins automatically; a missing sibling repo
   does not break the build.
2. **TuiSmoke (ConPTY):** `dotnet run --project e2e/TuiSmoke [path-to-agent.exe] [base-url]`
   launches the real `agent.exe --tui` inside a Windows pseudoconsole, sends
   real keystrokes and asserts rendering/behaviour (logo, /model picker,
   palette, commands, chat, clean exit). Add a new check when you change
   interactive behaviour.
   - ConPTY quirks (documented in the harness header — read it before
     touching it): `EXTENDED_STARTUPINFO_PRESENT` is required, the attribute
     value is the HPCON *value* not a pointer, close the pipe ends only after
     `CreateProcess`, and the child's attach is flaky when the harness output
     is redirected — run it with output to a real terminal.
3. **Real-terminal manual check** (the final word for visuals): run
   `agent --tui` in Windows Terminal, a Linux terminal and macOS Terminal,
   verify colours, scrolling, mouse, and that `/web` leaves the screen clean.
4. **Rules:** never assert localized strings in tests (they change per
   language); assert command names and stable markers (`ctx `, `sess:sess-`).

## 11. Cheat-sheet

| Goal | API |
|---|---|
| Create the app | `Application.Create().Init()` |
| Run a view / dialog | `app.Run(view)` |
| Close a view / dialog | `app.RequestStop(view)` |
| Defer work to the main loop (from background threads) | `app.Invoke(action)` (synchronous if already on the UI thread — §2) |
| Timer (one-shot/recurring) | `app.AddTimeout(ts, () => bool)` |
| Container that must host focusable children | `CanFocus = true` |
| Custom colour scheme | `SchemeManager.AddScheme(name, scheme)` + `view.SchemeName = name` |
| Consume a key | in `KeyDown`: `key.Handled = true` |
| Swallow unhandled keys from children | parent `KeyDownNotHandled` |
| Modal picker (single choice) | `Dialog` + `ListView` + `app.Run(dlg)` (see `RunPickerDialog`) |
| Checklist (multi-select) | `ListView.ShowMarks = true` + `IListDataSource.IsMarked/SetMark` |
| Password/API-key field | `TextField { Secret = true }` |
| Read-only scrollable text | `Editor { ReadOnly = true, WordWrap = true }` + focus/scroll handling |
| Multi-line prompt | `Editor { Multiline = true, WordWrap = true, GutterOptions = None }` |
| Bottom status line with key hints | `StatusBar(shortcuts)` |
| Simple message box | `MessageBox.Query(app, title, text, ok)` |
| Scroll a view programmatically | `view.ScrollVertical(offset)` |
| Platform check | `OperatingSystem.IsWindows()` (also IsLinux/IsMacOS) |

---

*Keep this guide in sync with reality: when you fix a Terminal.Gui bug in this
repo, add the lesson to §8. When you upgrade the packages, update §1 and
re-verify the API names against the new XML docs.*
