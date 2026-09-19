using System.Text;
using System.Text.Json;
using AIOrchestrator;
using AIOrchestrator.API;

// Enterprise real-case memory benchmark — AIOrchestrator deterministic retrieval.
//
// Models the real enterprise scenario the lab benchmarks ignore: an archive of MANY
// near-identical records (insurance practices / case files) that differ only by a
// key element (the practice number) plus a claimant name. In a real company there are
// thousands to millions of such records; the user always references a key (practice
// number, contract id, patient id). This measures whether the deterministic
// NameOrKey + word-index retrieval finds the EXACT record among the near-duplicates,
// instantly and reproducibly — with no LLM in the retrieval path.
//
// Synthetic data only (no private data). Reproducible via --seed.
//
// Metrics:
//   - indexing_time_ms : time to build the streaming word/vector index over N docs
//   - recall           : fraction of key-addressed queries whose exact record is found
//   - precision        : mean number of files returned per key query (ideally ~1)
//   - latency_ms       : mean FileSearch latency per query (no LLM)
//   - determinism      : identical results across two runs of every query
//   - ambiguity_demo   : searching by claimant name only returns many records (needs a key)

int records = 1000;
int queries = 100;
int seed = 42;
string outDir = Environment.GetEnvironmentVariable("ENT_OUT") ?? "out";

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--records": records = int.Parse(args[++i]); break;
        case "--queries": queries = int.Parse(args[++i]); break;
        case "--seed": seed = int.Parse(args[++i]); break;
        case "--out": outDir = args[++i]; break;
    }
}
Directory.CreateDirectory(outDir);
var rng = new Random(seed);

// --- Provider (used only if any LLM path is touched; retrieval here is LLM-free, so an
// empty key is acceptable — set SUPERFAST_API_KEY only if you extend this to LLM answering).
var apiKey = Environment.GetEnvironmentVariable("SUPERFAST_API_KEY") ?? "";
string baseUrl = Environment.GetEnvironmentVariable("SUPERFAST_BASE_URL") ?? "http://127.0.0.1:8000/";
string model = Environment.GetEnvironmentVariable("SUPERFAST_MODEL") ?? "gpt-4o-mini";
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

// --- Generate the synthetic enterprise archive.
var sandbox = Path.Combine(Path.GetTempPath(), "ent-bench-" + Guid.NewGuid().ToString("N"));
var casesDir = Path.Combine(sandbox, "cases");
Directory.CreateDirectory(casesDir);

string[] firstNames = { "Andrea", "Marco", "Giulia", "Luca", "Sara", "Davide", "Chiara", "Matteo", "Elena", "Paolo", "Federica", "Alessio", "Martina", "Simone", "Valentina" };
string[] lastNames = { "Rossi", "Bianchi", "Ferrari", "Russo", "Esposito", "Romano", "Colombo", "Ricci", "Marino", "Greco", "Bruno", "Gallo", "Conti", "De Luca", "Costa" };
string[] policies = { "Auto Insurance", "Home Insurance", "Health Plan", "Life Cover", "Travel Policy", "Business Liability" };
string[] statuses = { "Open", "Pending Review", "Approved", "In Processing", "Awaiting Documents" };
string[] adjusters = { "Adams", "Baker", "Carter", "Dawson", "Ellis", "Foster" };

var caseIds = new List<string>();
var caseClaimants = new List<string>();
var caseAmounts = new List<string>();
for (int r = 0; r < records; r++)
{
    var id = $"PR-{100000 + r}";
    var claimant = $"{firstNames[r % firstNames.Length]} {lastNames[(r / firstNames.Length) % lastNames.Length]}";
    var amount = (rng.Next(500, 50000) + rng.Next(0, 99) / 100.0).ToString("0.00");
    var opened = new DateTime(2022, 1, 1).AddDays(rng.Next(0, 1200)).ToString("yyyy-MM-dd");
    var policy = policies[rng.Next(policies.Length)];
    var status = statuses[rng.Next(statuses.Length)];
    var adjuster = adjusters[rng.Next(adjusters.Length)];
    var md = new StringBuilder();
    md.AppendLine($"# Case File {id}");
    md.AppendLine();
    md.AppendLine($"- Claimant: {claimant}");
    md.AppendLine($"- Policy Type: {policy}");
    md.AppendLine($"- Opened: {opened}");
    md.AppendLine($"- Claim Amount: {amount} EUR");
    md.AppendLine($"- Status: {status}");
    md.AppendLine($"- Adjuster: {adjuster}");
    md.AppendLine($"- Notes: Standard claim processed under the {policy} terms. Reference {id}.");
    File.WriteAllText(Path.Combine(casesDir, $"case_{id}.md"), md.ToString());
    caseIds.Add(id);
    caseClaimants.Add(claimant);
    caseAmounts.Add(amount);
}
Console.WriteLine($"[ent-bench] generated {records} synthetic case files in {casesDir}");

// --- Index the archive (background build), measure indexing time.
Setup.TrySetDocumentsPathEarly(sandbox, out _);
var idxSw = System.Diagnostics.Stopwatch.StartNew();
bool idle = Setup.WaitForIndexIdle(TimeSpan.FromMinutes(15));
idxSw.Stop();
Console.WriteLine($"[ent-bench] index idle={idle} in {idxSw.ElapsedMilliseconds} ms for {records} docs");

// --- Key-addressed retrieval via FileTool.FileSearch (deterministic, no LLM).
var fileTool = new FileTool();
int found = 0, totalReturned = 0, determinismOk = 0;
long latencySum = 0;
var perQuery = new List<object>();
int qn = Math.Min(queries, records);
for (int q = 0; q < qn; q++)
{
    int idx = rng.Next(records);
    var targetId = caseIds[idx];
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var res1 = fileTool.FileSearch(includeNameOrKeyElements: new List<string> { targetId });
    sw.Stop();
    var res2 = fileTool.FileSearch(includeNameOrKeyElements: new List<string> { targetId });

    bool hit = res1 != null && res1.Contains($"case_{targetId}.md");
    bool same = res1 == res2;
    int returned = CountFiles(res1);
    if (hit) found++;
    if (same) determinismOk++;
    totalReturned += returned;
    latencySum += sw.ElapsedMilliseconds;
    perQuery.Add(new { query_id = targetId, hit, determinism_same = same, files_returned = returned, latency_ms = sw.ElapsedMilliseconds });
}

// --- Ambiguity demo: search by a claimant name only (no key) → many records returned.
var demoClaimant = caseClaimants[0];
var ambRes = fileTool.FileSearch(path: "/cases", singleKeywords: new List<string> { demoClaimant.Split(' ')[1].ToUpperInvariant() });
int ambCount = CountFiles(ambRes);

double recall = qn > 0 ? 100.0 * found / qn : 0;
double precision = qn > 0 ? (double)totalReturned / qn : 0;
double meanLatency = qn > 0 ? (double)latencySum / qn : 0;
double determinism = qn > 0 ? 100.0 * determinismOk / qn : 0;

Console.WriteLine($"\n=== Enterprise deterministic retrieval ({records} near-identical records) ===");
Console.WriteLine($"  indexing_time_ms   {idxSw.ElapsedMilliseconds}  ({records} docs)");
Console.WriteLine($"  key-addressed recall   {recall:0.0}%  ({found}/{qn})");
Console.WriteLine($"  mean files returned    {precision:0.00}  (precision; ~1 = exact)");
Console.WriteLine($"  mean latency per query {meanLatency:0.0} ms  (no LLM)");
Console.WriteLine($"  determinism            {determinism:0.0}%  (identical across 2 runs)");
Console.WriteLine($"  ambiguity demo: name-only search for '{demoClaimant}' returned {ambCount} records (a key is required to disambiguate)");

var result = new
{
    records,
    queries = qn,
    seed,
    indexing_time_ms = idxSw.ElapsedMilliseconds,
    recall_pct = recall,
    mean_files_returned = precision,
    mean_latency_ms = meanLatency,
    determinism_pct = determinism,
    ambiguity_name_only_count = ambCount,
    per_query = perQuery
};
File.WriteAllText(Path.Combine(outDir, "enterprise_results.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"\n[ent-bench] wrote {Path.Combine(outDir, "enterprise_results.json")}");

// Cleanup sandbox.
try { Directory.Delete(sandbox, true); } catch { }

static int CountFiles(string? res)
{
    if (string.IsNullOrEmpty(res)) return 0;
    int c = 0;
    foreach (var line in res.Split('\n'))
        if (line.TrimStart().StartsWith("\"") && line.Contains(".md")) c++;
    return c;
}
