# AgentBridge Terminal UI — user guide

The console app of AgentBridge is an interactive terminal UI built on
[Terminal.Gui](https://github.com/tui-cs/Terminal.Gui) (v2, instance-based API),
modelled on [Qwen Code](https://github.com/QwenLM/qwen-code)'s TUI: a menu bar,
an AGENT logo panel, a streaming chat panel with an input line at the bottom, a
status bar, `/` slash commands with a **filterable command palette**, `@` file
attachments, keyboard shortcuts, mouse support and online help — while the HTTP
server keeps serving every other client on the same port. **CLI and API are the
same conversation**: messages you send from the UI go through the exact same
`POST /v1/chat/completions` endpoint that any OpenAI-compatible client uses, so
both can drive the agents at the same time (the UI holds one session; other
clients create their own).

## How Qwen Code's TUI works (the model we followed)

Qwen Code is a terminal-based agentic coding tool: you launch `qwen` in a
project and get a persistent TUI where:

- a **bottom input field** accepts plain-language prompts; a small **status
  line** shows the model/provider and context usage;
- typing **`/`** opens a **slash-command palette** — a contextual help list
  (name + one-line description per command) that **filters as you type**;
  Enter runs the highlighted command, Tab completes it, Esc closes it;
- **`@`** opens file/context completion;
- a rich **keyboard-shortcut layer** covers editing (Ctrl+A/E/U/K/W, word
  jumps), history (Up/Down, Ctrl+R reverse search), Ctrl+C cancel, Ctrl+L
  clear screen, `?` shortcuts overlay, Tab ghost-text completion;
- **`/model`** switches the LLM on the fly, `/help` shows the command docs,
  `/docs` opens the online documentation in the browser;
- agent replies **stream** into the conversation and can be cancelled.

## What AgentBridge does better

| Qwen Code | AgentBridge (this app) |
|---|---|
| TUI is the only front-end | TUI **and** the OpenAI-compatible API simultaneously, same process — any SDK/script can keep driving the agents while you chat |
| Model switch is provider-side | `/model` switches LLM **with a context-window guard** — the server refuses (409) when the conversation overflows the target provider's window and explains why |
| Mouse: scroll + click | native cross-platform mouse (Terminal.Gui): **wheel** scrolls the conversation and menus, **click** positions the input cursor or selects a list row, **double-click** runs the selected item |
| Menus | pickers are drawn inside the TUI layout — **Esc always cancels** (`/model`, `/agent`, `/attach`) with a clean screen, no residue |
| No voice | `/voice` — dictation from the server microphone (Windows), `/tts` — Kokoro neural TTS speaks the replies and plays the WAV |
| File completion via `@` | `@` palette of **uploaded** files (server-side `/v1/files`), `/files add <path>` uploads and attaches; attachments ride along as `file_ids` |
| Status shows context | status bar also shows **history tokens / context window**, agent set, TTS/mic availability, features |
| `/docs` opens docs site | `/docs` opens **this project's** online README; `/help` lists commands, shortcuts, API endpoints and links |
| — | `/agent` switches the agent tool set, `/features` toggles feature flags, `/health` pings the server, `/retry` resends the last prompt, `/web` opens the auto-connected web client |

## Commands (type `/` for the live list)

| Command | What it does |
|---|---|
| `/help` · `/?` | Full help: commands, shortcuts, API endpoints, online docs |
| `/docs` | Open the online documentation in the browser |
| `/web` | Install (first run) and launch the Giraffe AI web client, auto-connected to this server |
| `/modelsetup` | Configure LLM models & providers (add/edit/remove, active model, API keys) |
| `/model [name]` | Switch the LLM provider (menu when no name given; context-window checked) |
| `/agent [name]` | Switch the agent set (default/web/search/research/word/spreadsheet/email/multi) |
| `/voice [lang]` | Dictate from the server microphone into the input |
| `/tts [text]` | Speak the last agent reply (or the given text) — Kokoro TTS, WAV playback |
| `/features [name] [on\|off]` | Show or toggle session feature flags (voice, tts, ...) |
| `/new` · `/reset` | Start a new session (fresh conversation) |
| `/clear` | Reset the current session history (keeps the session) |
| `/status` | Session state + platform capabilities |
| `/files add <path>` · `/files rm <id>` · `/files` | Upload+attach a file, delete one, list uploads |
| `/attach [id]` | Toggle a file attachment for the chat (menu when no id) |
| `/shortcuts` · `/keys` | Keyboard shortcuts overlay (also press `?` on an empty input) |
| `/health` | Ping the server, report latency |
| `/retry` | Resend the last prompt (also Ctrl+Y) |
| `/exit` · `/quit` | Exit (also Ctrl+C twice, or Ctrl+D) |

## Web GUI

`/web` (or the menu **Web → GUI**) opens the agents in your browser. On first use it
downloads the [Giraffe AI](https://github.com/Graphene-Lab/GiraffeAI) client — a single
static `index.html` plus its own launcher — and extracts it into a `GiraffeAIWebClient`
folder next to the working directory. Then it runs the platform launcher
(`start.bat` / `start.sh`), which serves the client on `http://localhost:8000` and opens the
browser.

The launch passes `--provider` with this server's endpoint, so the client **registers the
AgentBridge provider (if not already present) and selects it immediately** — no manual
configuration, just start typing. A client installed before `--provider` support existed is
detected and re-downloaded automatically.

- The first download needs an internet connection (GitHub); afterwards the client is fully local.
- The client runs in its own launcher window/process and keeps serving after the TUI exits.
- The browser talks straight to this server (`POST /v1/chat/completions`), so CORS is enabled
  and no API key is needed for local use.

## Models & Providers setup

`/modelsetup` (or menu **File → Models & Providers**) opens a tabbed window that mirrors the
AIOffice settings panel:

| Tab | What you can edit |
|---|---|
| **LLM & Providers** | Active provider (dropdown, switches via the same path as `/model`), DeepSeek / Z.ai / Gemini API keys, and a provider list with **Add… / Edit… / Remove** — the CRUD operations apply immediately and persist to `providers.json` (see below) |
| **Email (SMTP)** | SMTP server, port, user, password and the recipient email |
| **Mail (IMAP)** | IMAP server, port, user and password |
| **General** | Step logging on/off (`logs/` folder) and the documents path (re-indexed on change) |

- Field edits (keys, email, general) apply when you press **Save**; **Close** discards them.
- Adding a provider opens a small form (name, protocol OpenAI/Gemini/Anthropic, model, base
  address, endpoint path, context window, timeout). Editing replaces the config in place;
  removing refuses to delete the last remaining provider. No API-key field: keys are wired
  to the three known names via `Setup.ApiKey`, so a dynamically added cloud provider cannot
  use one yet.
- The provider list also stays in sync with `GET /v1/models`, so an added provider can be
  switched to right away.

## Keyboard shortcuts

| Shortcut | Action |
|---|---|
| `Enter` | Send the message / run the selected command |
| `/` | Open the slash-command palette (live, filters as you type) |
| `@` | Open the file palette (toggle chat attachments) |
| `?` | Shortcuts overlay (empty input) |
| `Tab` | Complete the selected command in the palette |
| `Esc` | Close dialog · clear input · twice: exit |
| `Ctrl+C` | Cancel the reply · clear input · twice: exit |
| `Ctrl+D` | Exit (empty input) |
| `Ctrl+L` | Clear the session history (menu bar too) |
| `Ctrl+R` | Reverse-search prompt history |
| `Ctrl+Y` | Retry the last prompt |
| `Up` / `Down` | Prompt history (also Ctrl+P / Ctrl+N) |
| `←` / `→` | Move the cursor (with Ctrl: by word) |
| `Ctrl+A` / `Ctrl+E` | Select all / jump to end of the input |
| `Ctrl+U` / `Ctrl+K` | Delete to start / to end of the line |
| `Ctrl+W` | Delete the word before the cursor (also Ctrl+Backspace) |
| `PgUp` / `PgDn` | Scroll the conversation history |
| `F1` | Full help page |
| `F10` | Activate the menu bar |

The conversation auto-follows the stream while you are at the bottom; scrolling up
(wheel or `PgUp`) pauses the follow so you can read, and scrolling down or sending a
message resumes it.

## Mouse

Terminal.Gui provides native cross-platform mouse support: menus and dialogs are
rendered inside the layout and **Esc always cancels them** cleanly (`/model`,
`/agent`, `/attach`).

| Action | Effect |
|---|---|
| Mouse wheel (conversation) | Scroll the history |
| Mouse wheel (dialog/list) | Move the selection |
| Click the input line | Position the text cursor |
| Click a dialog/list row | Select it |
| Double-click a list row | Run it |

The terminal switches to the alternate screen buffer and restores it on exit.

## Launch modes

How the app starts — terminal UI, server only, or forced UI — is covered in
[docs/ARCHITECTURE.md](ARCHITECTURE.md#launch-modes).

---

See also: [README](../README.md) · [API reference](API.md) · [Architecture](ARCHITECTURE.md)
