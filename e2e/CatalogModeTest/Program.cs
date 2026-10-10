using AIOrchestrator;

// Focused diagnostic: run the agent with a tool-using task under a chosen provider +
// interaction mode, and report the result so the log can be analyzed for catalog
// correctness and tool-usage behavior (API native tools vs CLI single-tool).
//
// Usage:
//   CatalogModeTest --provider deepseek|ollama --mode api|cli [--force-text 0|1]
//                   [--tools FileTool,GitTool] [--prompt "..."] [--max-iter N]

string provider = "deepseek";
string mode = "api";
bool forceText = false;
var tools = new[] { "FileTool" };
string[] subagents = Array.Empty<string>();
string prompt = "Create a text file named probe.txt in the sandbox containing exactly the text CATALOG_TEST_123, then read it back and tell me its contents.";
int maxIter = 12;

bool dump = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--provider": provider = args[++i]; break;
        case "--mode": mode = args[++i]; break;
        case "--force-text": forceText = args[++i] == "1"; break;
        case "--tools": tools = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries); break;
        case "--subagents": subagents = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries); break;
        case "--prompt": prompt = args[++i]; break;
        case "--max-iter": maxIter = int.Parse(args[++i]); break;
        case "--dump": dump = true; break;
    }
}

if (dump)
{
    // Reflection dump: build the catalog and parse it into the native tools array, then print
    // each tool's name + required params, to verify the reserved-tool schemas (M1/M2) and the
    // optional-param handling (M3) directly, without a live LLM round-trip.
    var interactionModeDump = mode.Equals("cli", StringComparison.OrdinalIgnoreCase)
        ? AgentInteractionMode.CLI : AgentInteractionMode.API;
    var agentTypes = McpToolRegistry.ResolveAll(tools);
    var subTypes = subagents.Length > 0 ? McpToolRegistry.ResolveAll(subagents) : null;

    var harnessType = typeof(AgentHarness);
    var btc = harnessType.GetMethod("BuildToolCatalog", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
    using var hDump = new AgentHarness("DeepSeek");
    var catalogStr = (string)btc.Invoke(hDump, new object?[] { agentTypes, subTypes, interactionModeDump })!;
    Console.WriteLine($"[dump] catalog length={catalogStr.Length}");
    Console.WriteLine($"[dump] has '### get_skill' section: {catalogStr.Contains("### get_skill")}");
    Console.WriteLine($"[dump] has '### launch_subagent' section: {catalogStr.Contains("### launch_subagent")}");

    var llmType = typeof(LLMUtility);
    var ptc = llmType.GetMethod("ParseToolCatalog", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    var toolsList = (System.Collections.IList)ptc.Invoke(null, new object[] { catalogStr })!;
    Console.WriteLine($"[dump] parsed native tools = {toolsList.Count}");
    foreach (var t in toolsList)
    {
        var dict = (Dictionary<string, object>)t;
        var fn = (Dictionary<string, object>)dict["function"];
        var name = (string)fn["name"];
        var pars = (Dictionary<string, object>)fn["parameters"];
        var req = (List<string>)pars["required"];
        var propNames = ((Dictionary<string, object>)pars["properties"]).Keys;
        Console.WriteLine($"  - {name}: props=[{string.Join(",", propNames)}] required=[{string.Join(",", req)}]");
    }
    return;
}

var interactionMode = mode.Equals("cli", StringComparison.OrdinalIgnoreCase)
    ? AgentInteractionMode.CLI : AgentInteractionMode.API;

var cfg = provider.ToLowerInvariant() switch
{
    "deepseek" => new ProviderConfig
    {
        ProviderName = "DeepSeek",
        Protocol = ProviderProtocol.OpenAI,
        CacheType = ProviderCacheType.PrefixCache,
        ModelName = "deepseek-v4-flash",
        BaseAddress = new Uri("https://api.deepseek.com/"),
        EndPoint = "v1/chat/completions",
        ApiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") ?? "",
        ContextWindow = 1_000_000,
        Timeout = TimeSpan.FromMinutes(3),
        AgentInteractionMode = interactionMode,
        ForceTextToolDefinitions = forceText,
    },
    "ollama" => new ProviderConfig
    {
        ProviderName = "Ollama",
        Protocol = ProviderProtocol.OpenAI,
        CacheType = ProviderCacheType.PrefixCache,
        ModelName = "qwen3.5:4b",
        BaseAddress = new Uri("http://localhost:11434/"),
        EndPoint = "v1/chat/completions",
        ContextWindow = 32_000,
        Timeout = TimeSpan.FromMinutes(5),
        AgentInteractionMode = interactionMode,
        ForceTextToolDefinitions = forceText,
    },
    _ => throw new ArgumentException($"unknown provider '{provider}'")
};

ProviderConfigs.Upsert(cfg, persist: false);
Setup.ProviderConfig = cfg;

bool isSmall = cfg.ContextWindow < ProviderConfig.LargeContextThreshold;
bool expectText = isSmall || cfg.EffectiveAgentInteractionMode == AgentInteractionMode.CLI || cfg.ForceTextToolDefinitions;

Console.WriteLine($"[cmt] provider={cfg.ProviderName} model={cfg.ModelName} ctx={cfg.ContextWindow}");
Console.WriteLine($"[cmt] requested mode={mode} effective={cfg.EffectiveAgentInteractionMode} forceText={forceText} isSmall={isSmall} -> catalog sent as {(expectText ? "TEXT" : "NATIVE tools array")}");
Console.WriteLine($"[cmt] tools=[{string.Join(",", tools)}] subagents=[{string.Join(",", subagents)}] maxIter={maxIter}");
Console.WriteLine($"[cmt] prompt: {prompt}");

var sw = System.Diagnostics.Stopwatch.StartNew();
using var h = new AgentHarness(cfg.ProviderName);
var r = h.ExecuteAction(prompt, tools, subagentNames: subagents.Length > 0 ? subagents : null, maxIterations: maxIter);
sw.Stop();

Console.WriteLine($"[cmt] ---- RESULT ----");
Console.WriteLine($"[cmt] success={r?.Success} code={r?.Code} failureReason={r?.FailureReason} iterations={r?.Iterations} elapsed={sw.Elapsed.TotalSeconds:F1}s");
Console.WriteLine($"[cmt] message: {r?.Message}");
