# Book implementation map

This file says **where each example is recommended to go** inside the book
`Automate Your Business with AI` (`books/EN`). It is a guide only — in this phase we
produce the examples (markdown + PNG) and do **not** insert them into the book yet.

Each example lives in its own folder with a `README.md` (the story) and an `img/*.png`
(the illustration). To use one in the book, copy the story text into the target chapter
and place the PNG with a short caption.

## How to read the table

- **Example** = folder `dir/id`.
- **Book location** = the chapter file in `books/EN` where it fits best, or a **NEW**
  chapter we propose to add (see "Proposed new chapters" below).
- The book today is a general business-AI book. It has **no hands-on AgentBridge section**,
  so several examples need new chapters to be shown properly and to make the book a
  promotional showcase for AgentBridge.

## Examples that fit existing chapters

| Example | Book location (existing) | Why |
|---|---|---|
| 02-documents/client-invoice | `ch25-administration-and-finance.md` | Invoicing is core admin/finance |
| 02-documents/business-letter | `ch25-administration-and-finance.md` | Formal correspondence |
| 02-documents/service-contract | `ch32-professional-practice.md` | Client agreements |
| 02-documents/project-proposal | `ch26-sales-and-marketing.md` | Winning new work |
| 02-documents/meeting-minutes | `ch30-it-and-leadership.md` | Team/leadership admin |
| 02-documents/employee-handbook | `ch28-hr-and-people.md` | People/HR material |
| 03-spreadsheets/monthly-budget | `ch25-administration-and-finance.md` | Budgeting |
| 03-spreadsheets/sales-tracker | `ch26-sales-and-marketing.md` | Sales tracking |
| 03-spreadsheets/inventory-list | `ch29-operations-and-production.md` | Stock control |
| 03-spreadsheets/kpi-dashboard | `ch22-measuring-results-and-roi.md` | KPIs and results |
| 03-spreadsheets/timesheet | `ch28-hr-and-people.md` | Time and payroll |
| 04-presentations/pitch-deck | `ch26-sales-and-marketing.md` | Pitching |
| 04-presentations/sales-presentation | `ch26-sales-and-marketing.md` | Client decks |
| 04-presentations/training-deck | `ch28-hr-and-people.md` | Training |
| 04-presentations/board-review | `ch30-it-and-leadership.md` | Board reporting |
| 05-pdf-reports/market-analysis | `ch12-where-ai-can-help-your-business.md` | Analysis use case |
| 05-pdf-reports/financial-report | `ch22-measuring-results-and-roi.md` | Financial reporting |
| 05-pdf-reports/research-report | `ch12-where-ai-can-help-your-business.md` | Research use case |
| 06-email/quick-reply | `ch27-customer-care-and-support.md` | Customer replies |
| 06-email/follow-up | `ch26-sales-and-marketing.md` | Sales follow-up |
| 06-email/invoice-email | `ch25-administration-and-finance.md` | Sending invoices |
| 06-email/newsletter | `ch26-sales-and-marketing.md` | Marketing mail |
| 06-email/inbox-summary | `ch27-customer-care-and-support.md` | Inbox triage |
| 07-web-research/competitor-research | `ch12-where-ai-can-help-your-business.md` | Market research |
| 07-web-research/market-trends | `ch12-where-ai-can-help-your-business.md` | Trend watching |
| 07-web-research/due-diligence | `ch19-connecting-ai-to-systems-you-already-use.md` | Supplier checks |
| 07-web-research/news-digest | `ch12-where-ai-can-help-your-business.md` | Staying current |
| 07-web-research/product-compare | `ch17-choosing-tools-without-being-fooled.md` | Choosing tools |
| 08-maps/delivery-route | `ch29-operations-and-production.md` | Logistics |
| 08-maps/location-analysis | `ch33-retail-store.md` | Site selection |
| 08-maps/service-area | `ch34-services-and-consulting.md` | Coverage |
| 09-erp/order-status | `ch19-connecting-ai-to-systems-you-already-use.md` | System integration |
| 09-erp/customer-lookup | `ch27-customer-care-and-support.md` | Service on the phone |
| 09-erp/stock-check | `ch29-operations-and-production.md` | Stock availability |
| 10-cad/part-design | `ch31-small-manufacturing-business.md` | Product design |
| 10-cad/assembly-check | `ch31-small-manufacturing-business.md` | Fit checking |
| 10-cad/technical-drawing | `ch31-small-manufacturing-business.md` | Workshop drawings |
| 15-docs-memory/ask-your-documents | `ch14-data-the-raw-material.md` | Your data as knowledge |
| 15-docs-memory/find-in-archive | `ch14-data-the-raw-material.md` | Search your archive |
| 15-docs-memory/remember-preferences | `ch14-data-the-raw-material.md` | Memory |
| 15-docs-memory/version-history | `ch14-data-the-raw-material.md` | Safe editing |
| 16-web-api/web-chat | `ch19-connecting-ai-to-systems-you-already-use.md` | Different window |
| 16-web-api/officemanager-view | `ch30-it-and-leadership.md` | Overseeing agents |
| 16-web-api/http-api | `ch19-connecting-ai-to-systems-you-already-use.md` | Developer integration |
| 16-web-api/mcp-connector | `ch19-connecting-ai-to-systems-you-already-use.md` | Tool interop |
| 11-podcast/podcast-episode | `ch26-sales-and-marketing.md` | Content creation |
| 11-podcast/audio-briefing | `ch30-it-and-leadership.md` | Audio reporting |

## Proposed NEW chapters (needed for a complete showcase)

These capabilities have **no good home** in the current book. To show them properly — and
to make the book a clear promotional showcase for AgentBridge — we recommend adding the
following chapters. Suggested positions keep the flow from "what AI is" to "how to use it".

### NEW Part — "Meet AgentBridge: your office assistant"  (insert after `ch17-choosing-tools-without-being-fooled.md`)
A short, hands-on part that introduces the actual tool. Holds:
- `01-getting-started/first-chat` — the window and the first message
- `01-getting-started/choose-tools` — turning tools on (`/tools`)
- `01-getting-started/switch-model` — choosing the AI (`/model`)
- `01-getting-started/see-commands` — built-in help
- `01-getting-started/attach-file` — giving it a document (`@`)

Why here: after the reader has learned how to choose tools (ch17), showing a real,
easy tool lands perfectly and sets up every later example.

### NEW Chapter — "Scheduled tasks: work that runs by itself"  (insert after `ch12-where-ai-can-help-your-business.md`)
Holds the recurring-automation examples:
- `12-scheduling/daily-report`
- `12-scheduling/weekly-summary`
- `12-scheduling/recurring-reminder`
- `12-scheduling/monitor-website`

Why: the book talks about automation but never shows a task that runs on a schedule.
This is one of AgentBridge's strongest "wow" features.

### NEW Chapter — "Your assistant by voice and by phone"  (insert after the new Scheduled-tasks chapter)
Holds:
- `13-voice-phone/dictate-memo`
- `13-voice-phone/hear-the-answer`
- `13-voice-phone/call-your-agent`
- `13-voice-phone/hands-free-while-driving`

Why: voice and phone access are unique and very appealing; they need their own space.

### NEW Chapter — "Working on the go with Telegram"  (insert after the voice/phone chapter)
Holds:
- `14-telegram/chat-on-the-go`
- `14-telegram/send-a-file`
- `14-telegram/team-group`

Why: shows the assistant outside the office, which readers love; no current chapter covers it.

## Promotional note

The book should read as: *"here is how a non-technical person automates real office work,
and here is the tool that makes it easy."* Every example is written in plain English and
shows a finished result (a document, a chart, a reply, a route, a call). When inserting,
keep the **image first** and the **one-line "what you asked"** visible — that is what makes
the reader want to try AgentBridge themselves.
