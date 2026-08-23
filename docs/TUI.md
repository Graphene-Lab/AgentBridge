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
| Status shows context | status bar also shows **history tokens / context window**, the active **tools** (readable names, e.g. `File, Web, Git`), TTS/mic availability |
| `/docs` opens docs site | `/docs` opens **this project's** online README; `/help` lists commands, shortcuts, API endpoints and links |
| — | `/agent` opens the tools checklist (presets + individual tools), `/features` toggles feature flags, `/health` pings the server, `/retry` resends the last prompt, `/web` opens the auto-connected web client |

## Commands (type `/` for the live list)

| Command | What it does |
|---|---|
| `/help` · `/?` | Full help: commands, shortcuts, API endpoints, online docs |
| `/docs` | Open the online documentation in the browser |
| `/web` | Install (first run) and launch the Giraffe AI web client, auto-connected to this server |
| `/modelsetup` | Configure LLM models & providers (add/edit/remove, active model, API keys) |
| `/model [name]` | Switch the LLM provider (menu when no name given; context-window checked) |
| `/agent [name]` | Choose the agent tools: quick presets or an individual-tool checklist (Space toggles; see below) |
| `/voice [lang]` | Dictate from the server microphone into the input |
| `/tts [text]` | Speak the last agent reply (or the given text) — Kokoro TTS, WAV playback |
| `/telegram status\|config [set <key> <value>\|reload]\|login-code <code>\|allow\|disallow <user>` | Telegram chat medium: bare `/telegram` opens the interactive panel (status, login code, allow-list, config), the subcommands cover the same actions (see [Telegram](#telegram-chat)) |
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

## Agent tools

`/agent` (or menu **Tools → Agent & Tools**) opens a checklist dialog:

- **Quick presets** on top — the ready-made combinations (`default-agent`, `web-agent`,
  `document-agent`, …); Enter applies one immediately and its tools light up below.
- **Active tools** below — every tool actually loaded at runtime (core tools + the
  plugins in `Tools/`), each with a one-line description. **Space** toggles a tool;
  **Enter** applies the marked set as a **custom combination** (sent to the server as
  the additive `tools` field, which overrides the preset's `model`).

The status bar shows the active tools with readable names (`tools: File, Web, Git`), so
you always know what the agent can do in this conversation.

## Telegram chat

`/telegram` turns AgentBridge into a **Telegram chat client** (a userbot): people write to
the account in a **private chat**, the message (text and/or file attachments) goes through
the same per-user chat session as the TUI and the HTML client, and the reply — text plus
any files the agent attaches — comes back into the same chat. Text chat only: the Telegram
Client API has no audio-call support, so Telegram is **not** a voice medium (see
[docs/telegram.md](telegram.md)).

**Bare `/telegram`** (or menu **Tools → Telegram**) opens an interactive panel: live
status plus the first-login code field, allow/disallow user, config, reload and
enable/disable — no slash-command syntax to remember. The subcommands below drive the
same actions from the command line:

| Command | What it does |
|---|---|
| `/telegram status` | Live state: enabled, phase (`off`/`conn`/`code`/`2fa`/`on`/`err`), logged-in user, allow-list, agent |
| `/telegram config` | Show the effective configuration (api_hash masked) |
| `/telegram config set <key> <value>` | Change one config key and persist it to `telegram.json` (connection keys restart the bridge) |
| `/telegram config reload` | Re-read `telegram.json` (hand edits made outside the TUI) and apply them |
| `/telegram login-code <code>` | Complete the pending first login (verification code or 2FA password) |
| `/telegram allow <user>` · `/telegram disallow <user>` | Add / remove an allow-list entry (numeric id or `@username`) |

The **status bar** shows a `tg:` segment (`off`/`conn`/`code`/`2fa`/`on`/`err`), refreshed
by the same 3-second poll as SIP. When an agent reply carries attached files, the chat
history shows them as **`[attachment: <path>]`** lines — the files are saved under an
`attachments/` folder next to the executable.

The first login is TUI-guided: `/telegram status` shows `code` while the verification code
is pending, `/telegram login-code <code>` completes it (a 2FA password, if the account has
one, is submitted the same way), and the session persists in `telegram.session` — no code
is asked again. Configuration lives in `telegram.json` next to the executable (excluded
from updates); edit it by hand, with the setup scripts (`scripts/setup-telegram.bat` on
Windows, `scripts/setup-telegram.sh` on Linux/macOS), or with these commands. Full
reference: [docs/telegram.md](telegram.md).

## Models & Providers setup

`/modelsetup` (or menu **File → Models & Providers**) opens a tabbed window that mirrors the
AIOffice settings panel:

| Tab | What you can edit |
|---|---|
| **LLM & Providers** | Active provider (dropdown, switches via the same path as `/model`) and a provider list with **Add… / Edit… / Remove** — the CRUD operations apply immediately and persist to `providers.json` (see below) |
| **Email (SMTP)** | SMTP server, port, user, password and the recipient email |
| **Mail (IMAP)** | IMAP server, port, user and password |
| **General** | Step logging on/off (`logs/` folder) and the documents path (re-indexed on change) |

- Field edits (email, general) apply when you press **Save**; **Close** discards them.
- Adding a provider opens a small form (name, protocol OpenAI/Gemini/Anthropic, interaction
  mode Default/API/CLI, model, base address, endpoint path, **API key**, context window,
  timeout). The API-key field serves every cloud provider — any provider whose endpoint is
  **not** on loopback (`localhost` / `127.0.0.1`) needs one; local providers simply leave it
  empty. Keys are stored per-provider in `providers.json` (masked on screen while typing).
  Editing replaces the config in place; removing refuses to delete the last remaining
  provider. The interaction mode is optional: `Default` (the initial choice) leaves the
  decision to the model size — CLI for small models, API for large ones; `API`/`CLI` force
  one of the two. The active mode appears on the status page and is reported by
  `GET /v1/models` as `interaction_mode`.
- The provider list also stays in sync with `GET /v1/models`, so an added provider can be
  switched to right away.

## Auto-update

Menu **File → Auto-Update** toggles the automatic update check performed at startup
(checked = enabled, the default). The choice persists to the OS app-data folder
(`<AppData>\agent\autoupdate.json`), so it survives updates. When a newer release is
found, the app downloads it, swaps the files and restarts itself — see
[autoupdate.md](autoupdate.md) for the architecture and `--no-update` for services.

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

## Localisation

The TUI is fully localised for **EN, IT, FR, ES, DE, RU** using the standard .NET
resource-file approach (`Resources/Dictionary.resx` + per-language satellites). The app
runs in the **system language when supported, otherwise English** — the correct file is
selected automatically via `CultureInfo.CurrentUICulture` (a French system picks `fr`, a
German one `de`, any other culture falls back to the neutral English resource).

| File | Language |
|---|---|
| `Resources/Dictionary.resx` | English (neutral default) |
| `Resources/Dictionary.it.resx` | Italian |
| `Resources/Dictionary.fr.resx` | French |
| `Resources/Dictionary.es.resx` | Spanish |
| `Resources/Dictionary.de.resx` | German |
| `Resources/Dictionary.ru.resx` | Russian |

Rules and conventions:

- **Command names are never translated** — `/help`, `/model`, `/agent`, `/voice`, `/tts`,
  `/files`, … keep their English names in every language (they are also the API contract).
  Only the command *descriptions* shown in the palette/help are localised.
- All UI strings (menus, help pages, dialogs, status notes, picker hints) come from
  `Dictionary.*` (the strongly typed Designer generated from the resx). New UI strings go
  into the resx files, never hardcoded in `Tui.cs`.
- **System-generated agent results are localised too.** AIOrchestrator no longer returns
  hardcoded English messages ("Max iterations reached", "LLM returned no response", the
  "Done" fallback): it returns a locale-neutral `AgentResultCode` enum and AgentBridge maps
  each code to the phrase in the dictionary for the current language (see
  `AgentResult.cs` / `Program.cs` → `ResultText`). The agent's own LLM text passes through
  untouched, since the model is instructed to reply in the language of the request.
- Voice/TTS languages (`/voice`, `/tts`) keep following `SystemLang` (machine `CurrentUICulture`
  via `SystemLang.Get()`), independently of the UI dictionary.

To add a new language: copy `Resources/Dictionary.resx` to `Dictionary.XX.resx`
(XX = ISO 639-1 code), translate the values, and rebuild — the SDK picks the new satellite
up automatically.

## Launch modes

How the app starts — terminal UI, server only, or forced UI — is covered in
[docs/ARCHITECTURE.md](ARCHITECTURE.md#launch-modes).

---

See also: [README](../README.md) · [API reference](API.md) · [Architecture](ARCHITECTURE.md) · [Developer guide](TUI-DEVELOPMENT.md) (Terminal.Gui v2, for TUI code changes)
