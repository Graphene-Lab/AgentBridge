# Choosing the artificial intelligence that powers your assistant

AgentBridge works with many different AI providers, and you are free to choose the one that suits you best. You can use a cloud provider such as DeepSeek, Gemini, or Anthropic, or a local model that runs entirely on your own computer through programs like Ollama or ExLlamaV2. Cloud providers are powerful and need no special hardware, while local models keep everything on your machine, which is the most private option of all.

## Where to make the choice

Open the chat window and type /providers, or use the menu Settings and then LLM & Provider. A panel opens. At the top there is a dropdown that shows which provider is active right now: pick a provider there and press Save, and that provider becomes the active one — the one new conversations start with, and the choice is kept even after you close the panel or restart the app. Below the dropdown you see your list of providers, with the active one marked (attivo). From there you can add a new provider, edit an existing one, or remove one you no longer use.

## API keys

Most cloud providers require a key that identifies you. Open the LLM & Provider panel and you will see an "API key" box near the top: it shows the key of the provider you are working with — the one highlighted in the providers list, or the active provider in the dropdown when no row is highlighted. Click a provider in the list (or switch the active provider in the dropdown) and the box loads that provider's own key, so you always edit the right one; paste or change it and press Save. The key stays hidden while you type. You can also set it when you add or edit a provider from the list. Local providers, which run on your own computer, do not need a key, so you can leave the box empty for them. All your keys are stored on your machine and are never touched by an update.

## Switching at any time

You do not have to commit to a single provider. At any moment you can type /model followed by a name to switch, and the assistant immediately starts using the new one. If you ask for something longer than the provider can handle, AgentBridge refuses politely and explains why, instead of failing silently.

The next guide in this series shows you how to chat with your assistant and get the most out of your conversations.
