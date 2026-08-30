# Choosing the artificial intelligence that powers your assistant

AgentBridge works with many different AI providers, and you are free to choose the one that suits you best. You can use a cloud provider such as DeepSeek, Gemini, or Anthropic, or a local model that runs entirely on your own computer through programs like Ollama or ExLlamaV2. Cloud providers are powerful and need no special hardware, while local models keep everything on your machine, which is the most private option of all.

## Where to make the choice

Open the chat window and type /modelsetup, or use the menu File and then Models & Providers. A window opens with several tabs, and the one you need is called LLM & Providers. Here you can add a new provider, edit an existing one, or remove one you no longer use. The provider you mark as active is the one the assistant uses for your conversations.

## API keys

Most cloud providers require a key that identifies you. When you add a cloud provider, the window asks for its API key, and the key is hidden while you type. Local providers, which run on your own computer, do not need a key. All your keys are stored on your machine and are never touched by an update.

## Switching at any time

You do not have to commit to a single provider. At any moment you can type /model followed by a name to switch, and the assistant immediately starts using the new one. If you ask for something longer than the provider can handle, AgentBridge refuses politely and explains why, instead of failing silently.

The next guide in this series shows you how to chat with your assistant and get the most out of your conversations.
