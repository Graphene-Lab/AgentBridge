# Using AgentBridge from the AI app you already use

AgentBridge speaks the **Model Context Protocol**, the common language AI assistants use to work with external tools. That means you do not have to chat inside AgentBridge to benefit from it: if your assistant understands this protocol, you can keep working in the program you already know, and AgentBridge does the office work in the background. Copilot in VS Code and Claude Desktop are the two most common cases, and everything here applies to any other program of the same kind.

## A door is not a brain

The connection is only a door. It lets your assistant hand a request to AgentBridge and receive the answer. The work itself is done by the artificial intelligence you configured **inside** AgentBridge, working on the folder of documents you gave it. The connection supplies neither of the two, so set both before connecting anything.

### The artificial intelligence

Open AgentBridge, type `/providers` in the chat window, or use the menu **Settings**, then **LLM & Provider**. Choose a provider in the dropdown at the top, paste its key in the **API key** box and press **Save**. A cloud provider needs a key; a local one such as Ollama or ExLlamaV2 does not, but the program serving the model has to be running on your computer. The guide *Choosing the artificial intelligence that powers your assistant* walks through the choice.

If no provider is configured the connection still looks healthy, but every request comes back with a sentence such as **"No API key is configured for this provider"**. That is not the connection failing: it is AgentBridge telling you it has no brain to think with. The same happens with **"Could not reach the provider"** when the address points to a model that is not running, which is normal if you configured a local provider and then closed it.

### The documents area

Type `/general`, or **Settings**, then **General**, and look at the **Documents path**. That folder is what the agent can read, remember and write. Point it somewhere empty and the agent still answers, but only from what the model already knows: no contracts, no invoices, nothing of yours. The guide *Your documents area* explains indexing and what to expect the first time.

## Connecting Copilot in VS Code

1. Start AgentBridge and leave it running. The address it answers on is already listening: `http://localhost:5290/mcp`.
2. In VS Code open the Command Palette with `Ctrl+Shift+P`, run **MCP: Add Server**, choose **HTTP** and type `http://localhost:5290/mcp`, then give it a name such as `agentbridge`.
3. If you prefer a file, copy `examples/vscode-mcp.json` from the release into `.vscode/mcp.json` in your project: it already contains the same entry.
4. Open Copilot Chat in **agent mode**, open the tool picker and tick **`agent_run`**. Ask for what you need in plain language.

From that moment Copilot can delegate a whole office job to AgentBridge and read back the result, including the files it produced.

## Connecting Claude Desktop

Claude Desktop can only start programs that speak over the standard input and output of a process, while AgentBridge answers over HTTP. For this reason every release publishes a small forwarder:

1. Download **`agentbridge-mcp.mcpb`** from the [latest release](https://github.com/Graphene-Lab/AgentBridge/releases/latest).
2. In Claude Desktop open **Settings**, **Extensions**, **Advanced settings**, **Install extension** and choose the file. Node.js 18 or newer must be installed; the bundle is a script of about 15 KB and works the same on Windows, macOS and Linux.
3. Leave the endpoint as it is if you did not change the port, then enable the extension.

The bundle carries AgentBridge with it: it only forwards messages, so the program still has to be installed and running on the same computer. If it is not, Claude Desktop reports that the server cannot be reached.

## Connecting anything else

Any program that reads a JSON configuration can point at the endpoint directly when it supports HTTP, and can use the bundle by running `node index.js` with `AGENTBRIDGE_URL` set to `http://localhost:5290/mcp` when it only supports the standard input and output. Both forms are written out in the file `docs/MCP.md` that ships with AgentBridge.

If your program can install servers from the **Official MCP Registry**, search for `AgentBridge`: the entry offers both the small bundle and a container image, and the same two rules of the next paragraph apply.

## If you installed the container instead

The container image is a complete AgentBridge, but it is a separate machine, and two things are not inherited from your computer:

- **The model configuration.** The image starts with the standard provider list: empty keys, and addresses that point to the container itself instead of your computer. Copy a configured `providers.json` into the configuration volume (`/app/PersistentData`), and when the model runs on your computer use `http://host.docker.internal:PORT/` as the `BaseAddress` instead of `http://localhost:PORT/`.
- **Your documents.** Inside the container the agent reads `/documents`. Mount a folder of yours there, for example `-v "C:\Users\me\AgentBridge\documents:/documents"` on Windows or `-v "$HOME/AgentBridge/documents:/documents"` on Linux and macOS. Without that mount the documents area lives inside the container: the agent can still work, but you will never see the files it creates.

A local model is enough to try the container, but expect a single job to take minutes with a small one: it is the model that is slow, not the container.

## Keeping it safe

The address has **no password**. Anyone who can reach `http://localhost:5290/mcp` can run the agent, with every tool you enabled, and the agent can create, change and delete files in the documents area as you instructed it to. For this reason it listens only on your own computer: do not publish the port on a network, do not open it on the internet, and if you change the address restrict it at the network level. When you are done, close AgentBridge and the door is closed.

## When something does not work

| What you see | What it means |
|---|---|
| The program says the server cannot be reached, or the connection is refused | AgentBridge is not running, or it listens on another port. Start it, and check `http://localhost:5290/health` in a browser |
| Every answer is "No API key is configured for this provider" | No model is configured inside AgentBridge: see the first section of this guide |
| Every answer is "Could not reach the provider" | A provider is configured but its address is not answering: a local model that is not running, or a wrong address |
| "Max iterations reached" | The model kept working without finishing the job. Ask for something narrower, raise `max_iterations`, or use a stronger model |
| The job takes minutes | A request is a whole job, not a question: the agent plans, uses tools and writes files. With a small local model it is slow |
| A file the agent created is nowhere to be found | It was written in the documents area: check the **Documents path**, or the mounted folder if you use the container |

## What to expect

What the assistant receives is one tool, `agent_run`: you describe the goal, the agent decides which tools to use, and it keeps going even if you close the chat in the meantime. You can continue a conversation by passing the `session_id` you received back, and you can name a different model or provider for a single job. Nothing else of AgentBridge is exposed, so the assistant never sees more than the job it was asked to do.

The reference for hosts, options and the registry entry is the file `docs/MCP.md`; the everyday use of AgentBridge is described in the other guides of this series, and in the user manual.