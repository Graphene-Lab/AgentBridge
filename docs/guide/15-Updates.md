# Keeping AgentBridge up to date

AgentBridge takes care of its own updates. At every start it checks whether a newer version is available; when one is found, it downloads it, replaces the program files, and restarts itself. You do not need to download anything by hand or repeat any setup step. The whole process happens in the background and is designed so that if anything goes wrong, the current version simply keeps running.

## What an update never touches

This is the most important part: an update never touches your documents, your keys, or your settings. Your configuration files and your provider keys are protected by design and are never overwritten. Your files and folders stay exactly where they are. An update changes only the program itself.

## Asking for an update yourself

You can also check for a new version at any time instead of waiting for the next start:
type `/update` in the chat (or use the menu **Help → Check for updates…**). The program
checks GitHub immediately and tells you, in one short message, what happened:

- "Update X found — downloading…" with the download percentage in the status bar, then
  the app closes and restarts by itself on the new version — that closing is normal;
- "You are up to date" or "No newer version is published yet" — nothing to install;
- a message that explains a specific situation, e.g. the program was started with
  `dotnet` instead of `agent(.exe)`, GitHub could not be reached, agents are busy, or
  another AgentBridge instance is still running. When the message says to start the
  program with `agent.exe` (or `agent` on Linux/macOS), just restart it that way and run
  `/update` again.

When the program runs as a system service, an update is applied to the files and the
service restarts it — you do not have to do anything.

## Choosing how updates work

Automatic updates are enabled by default. If you prefer, you can turn the check off from the menu Help and then Auto-Update, and the choice is remembered even across updates. Some setups, such as a service that manages the program by itself, may prefer to handle updates separately; the program offers a way to skip the check for a single start when needed.

## A safe process

Updates are checked over a secure connection, and the old version is kept until the new one has started successfully, so there is always a way back. In the unlikely event that a new version fails to start, the previous one can be restored.

This is the end of the guide series. Every aspect of AgentBridge — from your first chat to scheduled work, from the phone to the podcast — has its own guide, and each one can be read on its own, whenever you need it.
