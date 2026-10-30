# AgentBridge as an MCP server

AgentBridge ships a native **Model Context Protocol** endpoint. The same process that runs
the agents answers MCP on:

- **Endpoint:** `POST http://localhost:5290/mcp`
- **Transport:** JSON-RPC 2.0 over HTTP `POST`, one JSON response per request; a message
  without `id` (a notification) is answered `204 No Content`. Stateless: no session header
  is issued or required.
- **Protocol version advertised by `initialize`:** `2025-06-18`
- **Registry name:** `io.github.Graphene-Lab/agentbridge` (Official MCP Registry)

There is no separate MCP service and no extra installation: start AgentBridge
(`agent.exe` on Windows, `agent` on Linux/macOS) and the endpoint is already listening on
the same port as the HTTP API.

> The endpoint has **no authentication** and is meant for `localhost`. Do not publish it on
> a public interface; if you change the binding, restrict it at the network level.

## Methods and tools

| Method | Purpose |
|---|---|
| `initialize` | Handshake; returns `protocolVersion`, `serverInfo` (`AgentBridge`) and capabilities |
| `tools/list` | Returns the exposed tools |
| `tools/call` | Runs a tool |

The exposed tool is **`agent_run`** — one autonomous AgentBridge execution for a prompt,
with every tool the agent has enabled (documents, spreadsheets, presentations, e-mail, web
research, …). The run continues on the server even if the MCP client disconnects; the answer
comes back as tool content.

| `agent_run` argument | Required | Notes |
|---|---|---|
| `prompt` | yes | The instruction for the agent |
| `model` | no | Default `default-agent` |
| `llm_provider` | no | Provider override for this run |
| `max_iterations` | no | 1..200 |
| `session_id` | no | Continue an existing multi-turn session |

The result carries `content` (text blocks), `structuredContent` (`success`, `code`,
`iterations`, `elapsed_ms`, `session_id`, `attachments`) and `isError`. The full wire
example (PowerShell) is in the [user manual](MANUAL.md#mcp-connector-standard-json-rpc).

A minimal call, straight over HTTP:

```bash
curl -s http://localhost:5290/mcp \
  -H 'content-type: application/json' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"agent_run","arguments":{"prompt":"Summarize this week sales in 5 bullets."}}}'
```

## Set up an MCP host

### VS Code / GitHub Copilot

Two equivalent ways, both pointing at the running endpoint:

1. Command Palette → **MCP: Add Server…** → **HTTP** → URL `http://localhost:5290/mcp`.
2. Or copy [`examples/vscode-mcp.json`](../examples/vscode-mcp.json) to `.vscode/mcp.json`
   in your workspace (or to your user `mcp.json`):

```json
{
  "servers": {
  "agentbridge": {
    "type": "http",
    "url": "http://localhost:5290/mcp"
  }
  }
}
```

Then start the server from the MCP view (or **MCP: List Servers → agentbridge → Start**)
and use Copilot Chat in agent mode: the `agent_run` tool appears in the tool picker.

### Claude Desktop

Claude Desktop launches local servers over **stdio**, which an HTTP endpoint cannot provide
directly. Use the bundle published with every release:

1. Download `agentbridge-mcp.mcpb` from the
   [latest release](https://github.com/Graphene-Lab/AgentBridge/releases/latest).
2. Claude Desktop → **Settings → Extensions → Advanced settings → Install extension…** and
   pick the file. Node.js 18+ must be installed (the bundle is a ~15 KB Node script, it
   carries no binaries, and it works the same on Windows, macOS and Linux).
3. Configure the endpoint in the extension settings if it is not
   `http://localhost:5290/mcp`, then enable the extension.

The bundle only bridges stdio to the local endpoint — the AgentBridge app still has to be
running. Without the bundle (or on a host that only reads a JSON config) the equivalent
manual entry is:

```json
{
  "mcpServers": {
  "agentbridge": {
    "command": "node",
    "args": ["/absolute/path/to/agentbridge-mcp/index.js"],
    "env": { "AGENTBRIDGE_URL": "http://localhost:5290/mcp" }
  }
  }
}
```

### Any other MCP client

- **HTTP native:** point it at `http://localhost:5290/mcp`.
- **Any client that needs stdio:** run the bundle's `index.js` with `node` (see above). It forwards
  every JSON-RPC message to `AGENTBRIDGE_URL` and writes the answers back to stdout;
  diagnostics go to stderr, so they never corrupt the protocol. It also accepts a
  `text/event-stream` answer from AgentBridge, so a streaming-capable host sees a normal
  stdio server.

## Run it in a container

Each release publishes the Linux build to `ghcr.io` as well, with the same tree as
`agentbridge-linux-x64.tar.gz` (binary, tool plugins, models) and the same MCP endpoint:

```bash
docker run -d --name agentbridge \
  -p 127.0.0.1:5290:5290 \
  -v agentbridge-data:/app/PersistentData \
  ghcr.io/graphene-lab/agentbridge:latest
```

- `-p 127.0.0.1:5290:5290` keeps the loopback binding. Do not expose the port: there is no
  authentication, and `agent_run` executes the agent's tools.
- `-v agentbridge-data:/app/PersistentData` holds the configuration, the provider keys and the
  sessions. Without it every container starts from the seeded defaults.
- The image is large (about 2.7 GB unpacked): it carries the self-contained .NET runtime, the ONNX
  models and every tool plugin. It is for a host that already runs Docker, not for a small VM.
- The entrypoint is `--headless --no-update`: no terminal inside, and the image owns its binaries.

The container speaks the same MCP endpoint on the same port, so an HTTP host still points at
`http://localhost:5290/mcp`. Give a stdio host the bundle with
`AGENTBRIDGE_URL=http://127.0.0.1:5290/mcp` once the container is up.

## The Official MCP Registry entry

[`server.json`](../server.json) is the registry manifest; a release updates it automatically
(see [RELEASING.md](../docs-dev/RELEASING.md)). It describes two ways to install the same server:

| Package | Identifier | Who should pick it |
|---|---|---|
| `mcpb` | the `agentbridge-mcp.mcpb` asset of the matching release | any host that installs desktop extensions; one archive, no binaries, Windows/macOS/Linux |
| `oci` | `ghcr.io/graphene-lab/agentbridge:<version>` | a client that runs containers and wants the whole app, tool plugins included |

A `remotes` (URL) entry is not possible: the registry only accepts it when the URL is publicly
reachable, and AgentBridge is local-first by design. The `oci` entry therefore declares a
`streamable-http` transport on `http://localhost:{PORT}/mcp`, because the container serves MCP over
HTTP rather than stdio.

The registry proves ownership of the image with an anonymous pull and by reading the
`io.modelcontextprotocol.server.name` label, which must match the `name` in `server.json`. Two
consequences: the ghcr package has to be **public**, and that label must never change. When the
image for a version is missing or not public yet, the release publishes the manifest with the
`mcpb` package only instead of failing.

Registry versions are strict semver (`X.Y.Z`), while the app version is `1.yy.MM.dd`: the
published version drops the leading `1.` and the leading zeros, so app `v1.26.10.22`
becomes registry `26.10.22` and `v1.26.10.05` becomes `26.10.5`. The registry always marks
the highest semver as `latest`, so the newest release is the one clients install.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `AgentBridge is not reachable at http://localhost:5290/mcp` (from the bundle) | The app is not running, or it listens on another port. Start it; check `curl http://localhost:5290/health` |
| Connection refused / `ECONNREFUSED` from any client | Same as above, or a firewall blocking loopback (rare) |
| 404 on `POST /mcp` | Wrong path: it is `/mcp`, not `/v1/mcp` or `/mcp/` |
| The host reports "no tools" | The server answered but `tools/list` was not called after `initialize`; restart the server in the host |
| A run takes minutes | `agent_run` is a full agent run (up to `max_iterations`). The default bridge timeout is 15 minutes, configurable with `AGENTBRIDGE_TIMEOUT_MS` |
| Claude Desktop: extension does not start | Node.js is missing from `PATH` (`node --version`), or the AgentBridge app is not running |
| Different port | Start AgentBridge with `--Urls http://localhost:PORT` (or `ASPNETCORE_URLS`), then use the same URL in the host configuration and in `AGENTBRIDGE_URL` |
| Docker: `initialize` answers but `tools/list` is empty | The container found no plugins: the build context must be the extracted archive, not an empty directory. Rebuild with `docker build -f docker/Dockerfile publish` |
| Docker: settings lost after `docker rm` | No volume on `/app/PersistentData` |

## Related

- [User manual](MANUAL.md#mcp-connector-standard-json-rpc) — the raw JSON-RPC example
- [HTTP API reference](API.md) — the OpenAI-compatible API on the same port
- [RELEASING.md](../docs-dev/RELEASING.md) — how a release publishes the MCP bundle and the container image
