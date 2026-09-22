# Project note — the example library

This folder holds the **example library** for the book *Automate Your Business with AI*.
It is built to show, in plain English, how AgentBridge automates real office work for
people with no technical or AI background.

## What is in here

- `catalog.json` — the data for all 63 examples (story, command, result, image spec).
- `build.js` — reads the catalog and writes every example folder (`README.md` + `img/*.png`).
- `_tools/gen.js` — renders one illustration (HTML → PNG) for each visual type.
- `_tools/tui_to_png.ps1` — an earlier TUI renderer (kept for reference; the TUI now
  renders through `gen.js` too).
- `_assets/tui/*.txt` — the raw TUI screen captures used by the getting-started examples.
- `01-…` to `16-…` — the example folders, grouped by topic.
- `book_implementation.md` — where each example goes in the book.
- `README.md` — the reader-facing index and the free-book download note.
- `TODO.md` — the next step.

## How the pictures were made

All pictures are **in English**, as required.

- **Getting-started pictures are real.** They were captured from the AgentBridge terminal
  (TUI) running in **puppet mode** (a DEBUG-only automation socket). To get English text on
  a machine set to Italian, the agent was launched through a small launcher that forces the
  `en-US` UI culture before the app starts — the product itself was not changed. The
  welcome screen, the `/tools` checklist, the Model setup window, the help screen and the
  `@` files palette are genuine captures.
- **The other pictures are clean illustrations** of the finished result (a document, a
  spreadsheet with a chart, a slide, a PDF report, an email, a map, a phone call, a
  Telegram chat, a browser view). They are rendered from HTML at 2× resolution with a
  headless browser, so they are sharp and consistent. They show what the agent produces,
  with realistic sample data.

## How to regenerate everything

From the repository root:

```text
node examples\build.js
```

This rewrites every `README.md` and re-renders every PNG from `catalog.json`. To change a
story or a picture, edit `catalog.json` (the prose) or the matching entry in the `IMAGES`
map inside `build.js` (the visual content), then run the build again.

## Conventions

- One folder per example: `NN-topic/id/README.md` + `NN-topic/id/img/id.png`.
- Simple, short English. No jargon. Every example names the tool and shows the result.
- Recurring tasks say so plainly: AgentBridge starts them at the agreed time.
- The English TUI launcher and the raw captures live under `_assets` and `_tools` and are
  **not** part of the shipped app — they are build-time helpers for this library only.
