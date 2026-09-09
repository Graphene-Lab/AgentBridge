// SetupPersistence.Test — in-process reproduction of AgentBridge issue #8
// ("setup bug": provider edits from the console not saved / not applied).
//
// The TUI /setup dialog writes providers through ProviderConfigs.Add/Upsert/SetDefault
// (persist:true). This harness drives those exact calls against a throwaway config
// directory and verifies the persistence + in-use-instance semantics the dialog relies on:
//   1. a full edit round-trip (Upsert with a modified ApiKey) survives a file reload;
//   2. editing must NOT silently drop fields the dialog does not expose
//      (CacheType, ForceTextToolDefinitions, PauseBetweenRequests — the issue symptom
//      "modifications not saving" = they were saved but the non-exposed fields vanished);
//   3. SetDefault persists the marker and re-points Setup.ProviderConfig (in-use instance);
//   4. an Upsert of the provider in use re-points Setup.ProviderConfig like SetApiKey does.
//
// Run: dotnet run --project e2e\SetupPersistenceTest
// Exit code 0 = all checks passed.
using AIOrchestrator;

var failures = 0;
void Check(string what, bool cond)
{
    Console.WriteLine($"  {(cond ? "✓" : "✗ FAIL")} {what}");
    if (!cond) failures++;
}

// Throwaway config dir: seed a providers.json that mirrors a real edited DeepSeekBridge
// (the non-exposed fields MUST survive an edit that only changes the ApiKey).
var dir = Path.Combine(Path.GetTempPath(), "AgentBridgeSetupPersistence", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
var file = Path.Combine(dir, "providers.json");
File.WriteAllText(file, """
[
  {
    "ProviderName": "DeepSeekBridge",
    "Protocol": "OpenAI",
    "CacheType": "PrefixCache",
    "ModelName": "deepseek-web/deepseek-chat",
    "BaseAddress": "http://127.0.0.1:8787/",
    "EndPoint": "v1/chat/completions",
    "Timeout": "00:05:00",
    "PauseBetweenRequests": "00:00:05",
    "ContextWindow": 1000000,
    "ForceTextToolDefinitions": true,
    "IsDefault": true,
    "ApiKey": "old-key"
  },
  {
    "ProviderName": "Gemini",
    "Protocol": "Gemini",
    "ModelName": "gemini-2.5-flash",
    "BaseAddress": "https://generativelanguage.googleapis.com/",
    "EndPoint": "v1beta/models",
    "ContextWindow": 1000000,
    "ApiKey": ""
  }
]
""");

// Point ProviderConfigs at the throwaway dir (the setter reloads from it).
ProviderConfigs.ConfigDirectory = dir;
Console.WriteLine("SetupPersistence.Test — AgentBridge issue #8 reproduction\n");

// AgentBridge initializes the in-use instance at startup (Program.cs: the default provider
// becomes Setup.ProviderConfig). Mirror that so the in-use-instance checks are meaningful.
Setup.ProviderConfig = ProviderConfigs.Get("DeepSeekBridge");
Check("harness: Setup.ProviderConfig initialized to the active provider",
    ReferenceEquals(Setup.ProviderConfig, ProviderConfigs.Get("DeepSeekBridge")));

// 0. Sanity: seed loaded with the non-exposed fields intact.
var seeded = ProviderConfigs.Get("DeepSeekBridge");
Check("seed: ForceTextToolDefinitions=true", seeded.ForceTextToolDefinitions);
Check("seed: PauseBetweenRequests=5s", seeded.PauseBetweenRequests == TimeSpan.FromSeconds(5));
Check("seed: IsDefault=true", seeded.IsDefault);
Check("seed: Default resolves to DeepSeekBridge", ProviderConfigs.Default.ProviderName == "DeepSeekBridge");

// 1. Full edit round-trip as the TUI edit dialog does (post-fix): build a NEW ProviderConfig
//    that changes the ApiKey. The FIXED dialog copies the fields it does not expose from the
//    existing instance (CacheType, ForceTextToolDefinitions, PauseBetweenRequests), so the
//    edit must not silently reset them. (The harness mirrors ShowProviderDialog's object
//    initializer, including the field-preservation fix.)
var existing = ProviderConfigs.Get("DeepSeekBridge");
var edited = new ProviderConfig
{
    ProviderName = "DeepSeekBridge",
    Protocol = ProviderProtocol.OpenAI,
    CacheType = existing.CacheType,                                   // fix: preserved
    AgentInteractionMode = null,
    IsDefault = existing.IsDefault,                                   // dialog: existing?.IsDefault ?? false
    ForceTextToolDefinitions = existing.ForceTextToolDefinitions,     // fix: preserved
    PauseBetweenRequests = existing.PauseBetweenRequests,             // fix: preserved
    ModelName = "deepseek-web/deepseek-chat",
    BaseAddress = new Uri("http://127.0.0.1:8787/"),
    EndPoint = "v1/chat/completions",
    ApiKey = "new-key",
    ContextWindow = 1000000,
    Timeout = TimeSpan.FromMinutes(5),
};
ProviderConfigs.Upsert(edited, persist: true);

// Re-read from disk (a restart reloads the file) and check what the edit left behind.
ProviderConfigs.Reset();
var afterReload = ProviderConfigs.Get("DeepSeekBridge");
Check("round-trip: ApiKey persisted as new-key", afterReload.ApiKey == "new-key");
Check("round-trip: IsDefault preserved", afterReload.IsDefault);
Console.WriteLine("  [diag] after edit reload — ForceTextToolDefinitions=" + afterReload.ForceTextToolDefinitions +
                  " PauseBetweenRequests=" + afterReload.PauseBetweenRequests);
Check("issue #8 fixed: ForceTextToolDefinitions survives the edit", afterReload.ForceTextToolDefinitions);
Check("issue #8 fixed: PauseBetweenRequests survives the edit", afterReload.PauseBetweenRequests == TimeSpan.FromSeconds(5));
Check("issue #8 fixed: CacheType survives the edit", afterReload.CacheType == existing.CacheType);

// 2. SetDefault persistence (the "set as default" console action). In the real app each
//    restart re-points Setup.ProviderConfig at the active provider (Program.cs startup);
//    after a file Reset the harness must do the same to model a restart faithfully.
ProviderConfigs.SetDefault("Gemini", persist: true);
ProviderConfigs.Reset();
Setup.ProviderConfig = ProviderConfigs.Get("DeepSeekBridge");   // restart: active provider
Check("SetDefault: marker persisted to Gemini", ProviderConfigs.Default.ProviderName == "Gemini");

// 3. In-session edit of the in-use provider (no restart): Upsert/SetApiKey/SetDefault must
//    re-point Setup.ProviderConfig at the NEW instance (issue #6 residual fix) so plugin
//    LLM calls and the next run see the edited key/endpoint without a switch.
var activeBefore = ProviderConfigs.Get("DeepSeekBridge");
Setup.ProviderConfig = activeBefore;
ProviderConfigs.SetApiKey("DeepSeekBridge", "key-via-setapikey", persist: true);
var afterApiKey = ProviderConfigs.Get("DeepSeekBridge");
Check("SetApiKey: Setup.ProviderConfig re-pointed to the updated instance",
    ReferenceEquals(Setup.ProviderConfig, afterApiKey) && !ReferenceEquals(afterApiKey, activeBefore));
ProviderConfigs.Upsert(new ProviderConfig
{
    ProviderName = "DeepSeekBridge",
    Protocol = ProviderProtocol.OpenAI,
    CacheType = afterApiKey.CacheType,
    IsDefault = afterApiKey.IsDefault,
    ForceTextToolDefinitions = afterApiKey.ForceTextToolDefinitions,
    PauseBetweenRequests = afterApiKey.PauseBetweenRequests,
    ModelName = "deepseek-web/deepseek-chat",
    BaseAddress = new Uri("http://127.0.0.1:8787/"),
    EndPoint = "v1/chat/completions",
    ApiKey = "key-via-upsert",
    ContextWindow = 1000000,
    Timeout = TimeSpan.FromMinutes(5),
}, persist: true);
Check("Upsert: Setup.ProviderConfig re-pointed to the upserted instance",
    ReferenceEquals(Setup.ProviderConfig, ProviderConfigs.Get("DeepSeekBridge")));
ProviderConfigs.SetDefault("Gemini", persist: true);
// By design SetDefault changes only the process-wide default for NEW sessions — the open
// session keeps its active provider (that is /model's job). So Setup.ProviderConfig must
// still point at DeepSeekBridge (the active provider), but at the NEW instance that no
// longer carries IsDefault.
Check("SetDefault(in-session): active provider unchanged, instance refreshed",
    ReferenceEquals(Setup.ProviderConfig, ProviderConfigs.Get("DeepSeekBridge"))
    && !ProviderConfigs.Get("DeepSeekBridge").IsDefault
    && ProviderConfigs.Default.ProviderName == "Gemini");

try { Directory.Delete(dir, true); } catch { }
Console.WriteLine(failures == 0 ? "\nALL OK — no persistence regression" : $"\n{failures} FAILURES");
Environment.Exit(failures == 0 ? 0 : 1);
