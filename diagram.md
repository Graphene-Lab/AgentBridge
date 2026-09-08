flowchart TD

subgraph group_host["Application host (single process, ASP.NET minimal host)"]
  node_program["Application host<br/>startup + wiring<br/>[Program.cs]"]
  node_httpapi["OpenAI-compatible /v1 HTTP API<br/>chat, files, models, audio/speech,<br/>voice/listen, control, mcp, health<br/>[Program.cs]"]
  node_config["Persistent configuration<br/>PersistentData\appsettings etc.<br/>[AppConfig.cs]"]
  node_apimodels["API request contracts<br/>[ApiModels.cs]"]
  node_officehub["OfficeManager hub<br/>WebSocket /ws/office + lifecycle tracker<br/>[OfficeBridge.cs]"]
  node_tts["In-process Kokoro TTS<br/>[TtsEngine.cs]"]
  node_voicebridge["Server-mic speech recognition<br/>one-shot, Windows<br/>[VoiceBridge.cs]"]
end

subgraph group_runtime["Agent runtime (AIOrchestrator engine)"]
  node_agent{{"AgentHarness engine<br/>per-conversation agent<br/>(AIOrchestrator, external NuGet)"}}
  node_sessions[("Shared session store<br/>session = AgentHarness + history<br/>[SessionStore.cs]")]
  node_stateless["Stateless transcript correlator<br/>[StatelessConversation.cs]"]
  node_agenttools["Agent presets<br/>tool-name tables<br/>[AgentTools.cs]"]
  node_tools["Tool registry + execution<br/>McpToolRegistry (engine)"]
  node_toolplugins["Dynamic tool plugins<br/>DocumentTool, SpreadsheetTool,<br/>OfficeTool — Tools/ folder<br/>[ToolPlugins.cs]"]
end

subgraph group_interfaces["User interfaces"]
  node_tui["Terminal GUI client<br/>HTTP client of the local server<br/>[Tui.cs]"]
  node_office["OfficeManager web app<br/>16-bit office, browser<br/>[OfficeManager/index.html]"]
  node_giraffe{{"Giraffe AI web client<br/>external repo Graphene-Lab/GiraffeAI<br/>installed at runtime, not in this repo"}}
end

subgraph group_channels["Channel bridges"]
  node_telegram["Telegram bridge<br/>MTProto userbot<br/>[TelegramBridge.cs]"]
  node_sip["SIP bridge<br/>SIPSorcery user agent<br/>[SipBridge.cs]"]
  node_sipvoice["SIP audio pipe<br/>[SipVoiceAgent.cs]"]
end

subgraph group_delivery["Distribution"]
  node_updater["App self-updater<br/>[AutoUpdate.cs]"]
  node_web_updater["Web client updater<br/>[WebClientUpdater.cs]"]
  node_release["Release automation<br/>CI release pipeline<br/>[release.yml]"]
end

node_voiceagent{{"AIOffice.VoiceAgent<br/>external subprocess<br/>(STT/TTS for SIP calls)"}}
node_providers{{"LLM providers<br/>external inference"}}
node_telegram_net(("Telegram network<br/>external service"))
node_sip_net(("SIP/RTP network<br/>external service<br/>[kamailio.cfg]"))

node_program -->|"loads"| node_config
node_program -->|"hosts"| node_httpapi
node_program -->|"initializes"| node_officehub
node_program -->|"ships and serves /OfficeManager"| node_office
node_program -.->|"background update check"| node_updater
node_program -.->|"background client install"| node_web_updater
node_httpapi -->|"binds"| node_apimodels
node_httpapi -->|"session_id chats"| node_sessions
node_httpapi -->|"stateless chats"| node_stateless
node_httpapi -->|"text-to-speech"| node_tts
node_httpapi -->|"voice/listen"| node_voicebridge
node_stateless -->|"transcript-hash to session"| node_sessions
node_sessions -->|"owns (AgentHarness + history)"| node_agent
node_agent -->|"resolves tool sets"| node_agenttools
node_agent -->|"executes tools"| node_tools
node_toolplugins -->|"registers plugin tools"| node_tools
node_agent -->|"LLM calls"| node_providers

node_tui -->|"conversations over local HTTP"| node_httpapi
node_tui -->|"opens browser"| node_office
node_tui -->|"launches client"| node_giraffe
node_office -->|"duplex WebSocket /ws/office"| node_officehub
node_officehub -->|"chat prompts"| node_sessions
node_officehub -->|"lifecycle events"| node_sessions
node_giraffe -->|"HTTP /v1/chat/completions"| node_httpapi

node_telegram -->|"per-user conversations"| node_sessions
node_telegram -->|"MTProto"| node_telegram_net
node_sip -->|"per-call conversations (VoiceConversation)"| node_sessions
node_sip -->|"signaling and RTP"| node_sip_net
node_sip -->|"call audio"| node_sipvoice
node_sipvoice -->|"JSON-lines audio / transcripts"| node_voiceagent

node_updater -->|"consumes platform archives"| node_release
node_web_updater -->|"installs/updates from GiraffeAI releases"| node_giraffe

click node_program "https://github.com/graphene-lab/agentbridge/blob/master/Program.cs"
click node_httpapi "https://github.com/graphene-lab/agentbridge/blob/master/Program.cs"
click node_config "https://github.com/graphene-lab/agentbridge/blob/master/AppConfig.cs"
click node_apimodels "https://github.com/graphene-lab/agentbridge/blob/master/ApiModels.cs"
click node_officehub "https://github.com/graphene-lab/agentbridge/blob/master/OfficeBridge.cs"
click node_tts "https://github.com/graphene-lab/agentbridge/blob/master/TtsEngine.cs"
click node_voicebridge "https://github.com/graphene-lab/agentbridge/blob/master/VoiceBridge.cs"
click node_sessions "https://github.com/graphene-lab/agentbridge/blob/master/SessionStore.cs"
click node_stateless "https://github.com/graphene-lab/agentbridge/blob/master/StatelessConversation.cs"
click node_agenttools "https://github.com/graphene-lab/agentbridge/blob/master/AgentTools.cs"
click node_toolplugins "https://github.com/graphene-lab/agentbridge/blob/master/ToolPlugins.cs"
click node_tui "https://github.com/graphene-lab/agentbridge/blob/master/Tui.cs"
click node_office "https://github.com/graphene-lab/agentbridge/blob/master/OfficeManager/index.html"
click node_telegram "https://github.com/graphene-lab/agentbridge/blob/master/TelegramBridge.cs"
click node_sip "https://github.com/graphene-lab/agentbridge/blob/master/SipBridge.cs"
click node_sipvoice "https://github.com/graphene-lab/agentbridge/blob/master/SipVoiceAgent.cs"
click node_sip_net "https://github.com/graphene-lab/agentbridge/blob/master/docs/sip-entry/kamailio.cfg"
click node_updater "https://github.com/graphene-lab/agentbridge/blob/master/AutoUpdate.cs"
click node_web_updater "https://github.com/graphene-lab/agentbridge/blob/master/WebClientUpdater.cs"
click node_release "https://github.com/graphene-lab/agentbridge/blob/master/.github/workflows/release.yml"
click node_giraffe "https://github.com/Graphene-Lab/GiraffeAI"

classDef toneNeutral fill:#f8fafc,stroke:#334155,stroke-width:1.5px,color:#0f172a
classDef toneBlue fill:#dbeafe,stroke:#2563eb,stroke-width:1.5px,color:#172554
classDef toneAmber fill:#fef3c7,stroke:#d97706,stroke-width:1.5px,color:#78350f
classDef toneMint fill:#dcfce7,stroke:#16a34a,stroke-width:1.5px,color:#14532d
classDef toneRose fill:#ffe4e6,stroke:#e11d48,stroke-width:1.5px,color:#881337
classDef toneIndigo fill:#e0e7ff,stroke:#4f46e5,stroke-width:1.5px,color:#312e81
class node_program,node_httpapi,node_config,node_apimodels,node_officehub,node_tts,node_voicebridge,node_sessions,node_stateless,node_agenttools,node_tools,node_toolplugins toneBlue
class node_tui,node_office toneAmber
class node_telegram,node_sip,node_sipvoice toneMint
class node_updater,node_web_updater,node_release toneRose
class node_agent,node_giraffe,node_voiceagent,node_providers,node_telegram_net,node_sip_net toneNeutral
