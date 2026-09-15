# Getting started with AgentBridge

AgentBridge is your own AI assistant. It runs on your computer, reads your documents, and helps you with everyday office work: drafting letters, building spreadsheets, answering questions about your files, and much more. Everything happens on your machine, so your documents stay private. You talk to it in plain language, the way you would write to a colleague, and it takes care of the rest.

## Downloading AgentBridge

Getting AgentBridge is simple. Open the download page at https://graphenelab.it/AgentBridge/download/ and the page automatically detects your operating system and offers you the right package. If you prefer the terminal, a single command does the same thing; the exact command for your system is shown on the download page.

The package is self-contained, which means you do not need to install anything else. The program itself, the voices used for speech, and every other component the assistant needs are already inside the package, about 460 megabytes in total.

## Starting AgentBridge

After the download finishes, extract the archive into a folder of your choice. On Windows, start the program by opening the file named agent.exe; on Linux or macOS, run the file named agent. Nothing else is required.

When AgentBridge starts, you will see a full-screen chat window, similar to a messaging application. At the same time, a small local server starts on your machine that other programs can use to speak with the same assistant. You do not need to worry about this detail, because everything works together automatically.

## If Windows blocks the app the first time

Windows has a built-in protection that watches programs you download from the internet. Because AgentBridge is not yet digitally signed with a commercial certificate (we are still arranging one), Windows may stop it the first time you open it and show a warning. This is normal for newer software that is not signed, and it does not mean the program is harmful. AgentBridge is open source, so anyone can read exactly what it does. Here is how to get past the warning.

**The blue "Windows protected your PC" window (SmartScreen).** This is the most common case. The window says that SmartScreen prevented an unrecognized app from starting. Click the small **More info** link in that window, and a **Run anyway** button appears. Click it and the program starts. You only need to do this once.

**Unblock the file instead.** If you prefer, right-click the file named agent.exe, choose **Properties**, and at the bottom of the General tab tick the **Unblock** checkbox, then click **Apply** and **OK**. This tells Windows the file is trusted, so it stops interrupting you. You can also do it for the whole folder from PowerShell with `Unblock-File` on the files inside it.

**"Smart App Control blocked an app" (Windows 11).** Smart App Control is a stricter protection that some Windows 11 computers have turned on. Unlike SmartScreen, it blocks every unsigned program and does not offer a "Run anyway" button for a single app. To run AgentBridge you need to turn Smart App Control off: open the Start menu, search for **Windows Security**, go to **App & browser control**, open **Smart App Control settings**, and choose **Off**. Please read this before you do it: once Smart App Control is turned off it cannot be turned back on again without reinstalling Windows, so only turn it off if you trust where you downloaded the program from. Downloading AgentBridge from the official download page or the official GitHub releases page is the trusted source.

If you would rather not change any Windows setting, the one-line installer described above runs the download and setup for you and is the smoothest way to get going on Windows.

## The first start

The first time you start AgentBridge, the assistant begins reading your documents folder in the background. If you have a large collection of files, this first indexing can take a few minutes, but you can start chatting right away while it works. From that moment on, the assistant can answer questions using the information found in your own files, instead of guessing.

## What happens next

The assistant is ready when you are. Type your first request in plain language and see what it can do: ask it to write a letter, summarise a report, or prepare a spreadsheet. AgentBridge also keeps itself up to date automatically, and an update never touches your documents, your keys, or your settings.

The next guide in this series explains how to choose the artificial intelligence that powers your assistant and how to connect your favourite provider.
