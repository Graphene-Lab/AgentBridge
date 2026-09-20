using System.Text;
using System.Text.Json;
using AIOrchestrator;

// LongMemEval-S harness — ARCHITECTURE-FAITHFUL path.
// Models the real AIOrchestrator long-term-memory design: the dataset is treated as a
// company's classified documentation placed in the sandbox documents area, indexed into
// the streaming word/vector index, and the AGENT answers by locating the relevant
// document(s) with FileTool.FileSearch + ReadFile. This is the retrieval mechanism the
// architecture actually uses for "the answer is in the archive" — NOT the key-addressable
// NameOrKey memory store (that path is covered by the sibling LongMemEvalHarness).
//
// Per instance:
//   1. fresh sandbox dir; write each haystack session as a markdown document
//   2. point Setup.DocumentsPath at it; wait for the background index to go idle
//   3. run the agent with FileTool; the prompt states the answer is in the archive
//   4. capture AgentResult.Message as the hypothesis
// Output: hypotheses.jsonl + a detailed run.log.jsonl.

var apiKey = Environment.GetEnvironmentVariable("SUPERFAST_API_KEY") ?? "";
if (string.IsNullOrEmpty(apiKey))
{
    Console.Error.WriteLine("Set SUPERFAST_API_KEY (and optionally SUPERFAST_BASE_URL, SUPERFAST_MODEL) to an OpenAI-compatible provider before running.");
    Environment.Exit(2);
}
string baseUrl = Environment.GetEnvironmentVariable("SUPERFAST_BASE_URL") ?? "http://127.0.0.1:8000/";
string model = Environment.GetEnvironmentVariable("SUPERFAST_MODEL") ?? "gpt-4o-mini";
string dataPath = Environment.GetEnvironmentVariable("LME_DATA") ?? "longmemeval_s_cleaned.json";
string outDir = Environment.GetEnvironmentVariable("LME_OUT") ?? "out";
int perCategory = 1;
int maxSessions = 0;
List<string>? onlyCategories = null;
bool smoke = false;
int shardIndex = 0;
int shardCount = 1;
int maxIterations = 20;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--data": dataPath = args[++i]; break;
        case "--out": outDir = args[++i]; break;
        case "--per-category": perCategory = int.Parse(args[++i]); break;
        case "--max-sessions": maxSessions = int.Parse(args[++i]); break;
        case "--categories": onlyCategories = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(); break;
        case "--smoke": smoke = true; break;
        case "--shard-index": shardIndex = int.Parse(args[++i]); break;
        case "--shard-count": shardCount = int.Parse(args[++i]); break;
        case "--max-iterations": maxIterations = int.Parse(args[++i]); break;
    }
}
if (smoke) { perCategory = 1; onlyCategories = new List<string> { "single-session-user" }; }

Directory.CreateDirectory(outDir);
var hypPath = Path.Combine(outDir, "hypotheses.jsonl");
var logPath = Path.Combine(outDir, "run.log.jsonl");
File.WriteAllText(hypPath, "");
File.WriteAllText(logPath, "");

var cfg = new ProviderConfig
{
    ProviderName = "SUPERFAST",
    Protocol = ProviderProtocol.OpenAI,
    CacheType = ProviderCacheType.PrefixCache,
    NoJsonResponseFormat = true,
    ExtraBody = new Dictionary<string, object> { ["reasoning_effort"] = "low" },
    ModelName = model,
    BaseAddress = new Uri(baseUrl),
    EndPoint = "v1/chat/completions",
    ApiKey = apiKey,
    ContextWindow = 262144,
    Timeout = TimeSpan.FromMinutes(10),
    PauseBetweenRequests = TimeSpan.Zero
};
ProviderConfigs.Upsert(cfg, persist: false);
Setup.ProviderConfig = cfg;

Console.WriteLine($"[agent-harness] provider={cfg.ProviderName} model={cfg.ModelName} data={dataPath}");

using var fs = File.OpenRead(dataPath);
var doc = JsonDocument.Parse(fs);
var instances = new List<JsonElement>(doc.RootElement.EnumerateArray());
Console.WriteLine($"[agent-harness] loaded {instances.Count} instances");

var byCat = new Dictionary<string, int>();
var selected = new List<JsonElement>();
foreach (var inst in instances)
{
    var qid = inst.GetProperty("question_id").GetString() ?? "";
    var cat = inst.GetProperty("question_type").GetString() ?? "?";
    bool isAbs = qid.EndsWith("_abs");
    var key = isAbs ? "abstention" : cat;
    if (onlyCategories != null && !onlyCategories.Contains(key)) continue;
    byCat.TryGetValue(key, out var n);
    if (n >= perCategory) continue;
    byCat[key] = n + 1;
    selected.Add(inst);
}
if (shardCount > 1)
    selected = selected.Where((_, i) => i % shardCount == shardIndex).ToList();
Console.WriteLine($"[agent-harness] selected {selected.Count} instances: {string.Join(", ", byCat.Select(kv => kv.Key + "=" + kv.Value))} (shard {shardIndex}/{shardCount})");

var jsonOpts = new JsonSerializerOptions { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
bool firstInstance = true;
int done = 0;
foreach (var inst in selected)
{
    var qid = inst.GetProperty("question_id").GetString() ?? "";
    var cat = inst.GetProperty("question_type").GetString() ?? "?";
    var question = Str(inst.GetProperty("question"));
    var gold = Str(inst.GetProperty("answer"));
    var qdate = Str(inst.GetProperty("question_date"));

    // Fresh sandbox archive for this instance (isolation).
    var sandbox = Path.Combine(Path.GetTempPath(), "lme-docs-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(sandbox);

    var sessions = inst.GetProperty("haystack_sessions").EnumerateArray().ToList();
    var dates = inst.GetProperty("haystack_dates").EnumerateArray().Select(d => d.GetString() ?? "").ToList();
    int nSess = sessions.Count;
    if (maxSessions > 0 && nSess > maxSessions) nSess = maxSessions;

    var sw = System.Diagnostics.Stopwatch.StartNew();

    // 1. Write each haystack session as a markdown document in the sandbox.
    int filesWritten = 0;
    for (int s = 0; s < nSess; s++)
    {
        var turns = sessions[s].EnumerateArray().ToList();
        var date = s < dates.Count ? dates[s] : "";
        var md = new StringBuilder();
        md.AppendLine($"# Conversation session {s + 1:D3} — {date}");
        md.AppendLine();
        foreach (var t in turns)
        {
            var role = t.GetProperty("role").GetString() ?? "user";
            var content = t.GetProperty("content").GetString() ?? "";
            var who = role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "Assistant" : "User";
            md.AppendLine($"**{who}:** {content}");
            md.AppendLine();
        }
        File.WriteAllText(Path.Combine(sandbox, $"session_{s + 1:D3}.md"), md.ToString());
        filesWritten++;
    }

    // 2. Point the sandbox at this archive and wait for the background index to finish.
    if (firstInstance)
    {
        Setup.TrySetDocumentsPathEarly(sandbox, out _);
        firstInstance = false;
    }
    else
    {
        Setup.DocumentsPath = sandbox;
    }
    bool idle = Setup.WaitForIndexIdle(TimeSpan.FromMinutes(8));

    // 2b. Hybrid memory: populate the NameOrKey memory from the archive the way production
    // does at conversation end, so the agent runs with BOTH deterministic memory and file
    // retrieval active — the real hybrid. Keyless questions surface nothing from memory (by
    // design); keyed ones get the remembered fact injected automatically.
    try
    {
        var memEntries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(sandbox, "session_*.md"))
        {
            var text = File.ReadAllText(f);
            foreach (var key in RagDocumentProcessor.ExtractNameOrKeyElementsFromContent(text))
            {
                if (memEntries.ContainsKey(key) || memEntries.Count >= 500) continue;
                var line = text.Split('\n').FirstOrDefault(l => l.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) ?? key;
                line = line.Trim();
                memEntries[key] = line.Length > 240 ? line.Substring(0, 240) : line;
            }
        }
        var memDir = Path.Combine(sandbox, ".mem");
        Directory.CreateDirectory(memDir);
        File.WriteAllText(Path.Combine(memDir, "memory.json"),
            JsonSerializer.Serialize(memEntries.Select(kv => new { key = kv.Key, info = kv.Value }), jsonOpts));
    }
    catch (Exception mex) { Console.WriteLine($"[hybrid-memory] population skipped: {mex.GetType().Name}: {mex.Message}"); }

    // 3. Run the agent with FileTool; the answer is stated to be in the archive.
    string hypothesis = "";
    int iterations = 0;
    bool agentOk = false;
    try
    {
        using var h = new AgentHarness("SUPERFAST");
        var ap = new StringBuilder();
        ap.AppendLine("You are an enterprise knowledge assistant. The answer to the question is stored somewhere in your document archive.");
        ap.AppendLine("How to find it:");
        ap.AppendLine("1. Call search_context with the key terms from the question (proper names, distinctive nouns and verbs). It returns the passages where those terms appear, with surrounding context — this usually contains the answer directly.");
        ap.AppendLine("2. If that is not enough, call file_search (path=\"/\" with singleKeywords, or NameOrKey elements) to list candidate documents, then read_file on the most relevant ones.");
        ap.AppendLine("3. If the question asks HOW MANY, or needs facts combined from several places: do NOT answer from a single search. Run search_context several times with different keyword variants and a larger maxPassages (e.g. 20), read the passages, and enumerate every distinct instance before giving the count — missing one instance gives a wrong count.");
        ap.AppendLine("4. For a recommendation or preference question (what would I like, suggest something for me), the answer is the user's own stated taste, not the literal thing asked. Search for how the user describes their preferences (e.g. \"I like\", \"I prefer\", \"my favorite\", \"I love\", \"I enjoy\") together with the relevant topic, and answer by applying that preference — even if the exact item named in the question never appears in the archive.");
        ap.AppendLine("5. Always end with a clear final answer. If you truly cannot find it after searching, say that you do not know — never return an empty answer.");
        ap.AppendLine();
        ap.AppendLine($"Question (asked on {qdate}): {question}");
        ap.AppendLine();
        ap.AppendLine("Answer concisely using only what you find in the archive.");
        var r = h.ExecuteAction(ap.ToString(), new[] { "FileTool" }, maxIterations: maxIterations);
        hypothesis = (r?.Message ?? "").Trim();
        iterations = r?.Iterations ?? 0;
        agentOk = r?.Success ?? false;
    }
    catch (Exception ex)
    {
        hypothesis = $"(agent error: {ex.GetType().Name}: {ex.Message})";
    }

    sw.Stop();

    File.AppendAllText(hypPath, JsonSerializer.Serialize(new { question_id = qid, hypothesis }, jsonOpts) + "\n");
    var rec = new
    {
        question_id = qid,
        question_type = cat,
        n_sessions = nSess,
        files_written = filesWritten,
        index_idle = idle,
        agent_ok = agentOk,
        iterations,
        hypothesis,
        gold,
        elapsed_ms = sw.ElapsedMilliseconds
    };
    File.AppendAllText(logPath, JsonSerializer.Serialize(rec, jsonOpts) + "\n");

    done++;
    Console.WriteLine($"[{done}/{selected.Count}] {qid} ({cat}) sess={nSess} files={filesWritten} idle={idle} iters={iterations} ok={agentOk} {sw.ElapsedMilliseconds}ms");
    Console.WriteLine($"    Q: {question}");
    Console.WriteLine($"    H: {Truncate(hypothesis, 220)}");
    Console.WriteLine($"    G: {Truncate(gold, 220)}");

    try { Directory.Delete(sandbox, true); } catch { }
}

Console.WriteLine($"[agent-harness] done. hypotheses -> {hypPath}");
Console.WriteLine($"[agent-harness] log      -> {logPath}");

static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";
static string Str(JsonElement e) => e.ValueKind == JsonValueKind.String ? (e.GetString() ?? "") : e.ToString();
