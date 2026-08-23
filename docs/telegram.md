# Telegram chat — AgentBridge as a Telegram chat client

AgentBridge can connect to Telegram **as a user account** (a "userbot") and act like any
other chat client you already support (the TUI, the HTML/Giraffe web client): people write
to your account in a **private chat**, the message (text and/or file attachments) is handed
to the agents, and the reply — text plus any files the agent attaches — comes back into the
same chat.

This is **text chat with attachments only**. The Telegram Client API (MTProto) does not
support audio calls, so Telegram is **not** a voice medium: the media list stays
**SIP (phone calls) and Voice (desktop microphone)** — Telegram adds a chat client, nothing
more. The transport is the [WTelegramClient](https://github.com/wiz0u/WTelegramClient)
library (userbot, MTProto); no HTTP/API polling is involved.

## How it works

```
Telegram user ⇄ private chat message (text + files) ⇄ WTelegramClient (userbot)
                                                          │
                              filter: private chats only, own messages (echo) ignored
                                                          │
                                      allow-list gate (AllowedUsers; empty = everyone)
                                      — disallowed users are silently ignored
                                                          │
                              per-user FIFO queue (messages answered in arrival order)
                                                          │
        DownloadFileAsync (incoming files, ≤25 MB) → FileAttachment     │
                                                          │
                                              SessionStore + AgentHarness.ExecuteAction
                                              (one chat session per user, 30-min idle expiry)
                                                          │
        SendMessageAsync (reply) + SendMediaAsync (agent's attachments, ≤25 MB each)
```

- **Private chats only.** Messages in groups/channels are ignored (as are the bot's own
  messages, so replies never echo into an endless loop).
- **Allow-list (optional).** `AllowedUsers` in `telegram.json` restricts who can talk to
  the agent (numeric user id or `@username`). **Empty = everyone** in a private chat, exactly
  like the HTML client. Disallowed users are silently ignored.
- **Attachments both ways.** Incoming documents/photos are downloaded (cap 25 MB) and go
  through the same server-side Markdown conversion as the HTML uploads (`/v1/files`), so the
  agent reads their content. Files the agent attaches in its answer (the `done` method's
  `"attachments"` field) are sent back as Telegram documents (cap 25 MB each).
- **One conversation per user.** Each user keeps a multi-turn session (history); after
  30 minutes of silence the session is disposed and a fresh one starts on the next message.

## Configuration (`telegram.json`)

Telegram configuration lives in its own file, **`telegram.json` next to the executable**
— separate from `appsettings.json` on purpose, and never overwritten by updates (see
RELEASING.md "what an update must never touch", same protection as `providers.json`).
You can edit it by hand, from the TUI (`/telegram`), or with the guided setup scripts
(`scripts/setup-telegram.bat` on Windows, `scripts/setup-telegram.sh` on Linux/macOS —
English prompts, they create or update `telegram.json`).

| Key | Default | Description |
|---|---|---|
| `Enabled` | `false` | Master switch — the bridge starts at boot only when true |
| `ApiId` | `0` | App api_id from https://my.telegram.org/apps |
| `ApiHash` | `""` | App api_hash from https://my.telegram.org/apps |
| `PhoneNumber` | `""` | Account phone number, international format (e.g. `+393331234567`) |
| `SessionPath` | `"telegram.session"` | Session file (auth keys) relative to the executable dir. After the first login the session persists: no code is asked again |
| `AllowedUsers` | `[]` | Users allowed to talk to the agent — numeric ids and/or `@usernames`, comma-separated in the TUI. Empty = all private chats |
| `Agent` | `"default-agent"` | Agent set used for the conversations (see AgentTools.Resolve) |

## First login (one time only)

The first login needs the **verification code** Telegram sends (SMS/call/other Telegram
app), and the 2FA password if the account has one. The TUI guides it — nothing blocks the
server boot, the bridge simply waits in a pending-login state:

```
/telegram status                        → phase "code" (login pending)
/telegram login-code 12345              → paste the code from Telegram
/telegram status                        → phase "on" (connected)
```

The `.session` file is written automatically; the next starts log in silently.

## TUI commands

| Command | Meaning |
|---|---|
| `/telegram status` | Live state: enabled, phase (`off`/`conn`/`code`/`2fa`/`on`/`err`), logged-in user, allow-list, agent |
| `/telegram config` | Show the effective configuration (api_hash masked) |
| `/telegram config set <key> <value>` | Change one config key and persist it to `telegram.json` (connection keys restart the bridge) |
| `/telegram config reload` | Re-read `telegram.json` (hand edits made outside the TUI) and apply them |
| `/telegram login-code <code>` | Complete the pending login (verification code or 2FA password) |
| `/telegram allow <user>` | Add a user (id or @username) to the allow-list and persist |
| `/telegram disallow <user>` | Remove a user from the allow-list and persist |

The status bar shows a `tg:` segment (`on` = connected, `code` = waiting for the login
code, ...) refreshed by the same 3-second poll as SIP.

**Telegram is an in-process chat client — it exposes no HTTP endpoints.** The
`/telegram` TUI commands call the `TelegramBridge` directly in the same process; the
message transport is entirely the WTelegramClient library. There is nothing to configure
over HTTP: the configuration surface is the TUI, the setup scripts, and `telegram.json`
itself.

## Getting your api_id / api_hash

1. Open https://my.telegram.org/apps and sign in with the account you want to use.
2. Create an application (any name/description — these identify *your* app, not the user).
3. Copy **api_id** and **api_hash** into `telegram.json` (or answer the setup script).

## Notes and limitations

- **A userbot, not a bot.** The bridge signs in as a real user account. Telegram's terms
  of service apply; don't use it for spam. If you prefer a bot account, use a BotFather
  token instead — out of scope here.
- **No audio.** The Telegram Client API has no audio-call support: voice messages are
  treated as file attachments, not as a conversation medium.
- **Security.** The api_hash and the `.session` file are credentials: protect them like the
  API keys in `providers.json`. The session file allows full access to the account.
- **Telegram sessions are per-device.** Telegram may show a new active session in the
  account settings after the first login — normal.
