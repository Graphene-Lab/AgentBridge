// ═══════════════════════════════════════════════════════════════════════
//  SipSmoke — end-to-end smoke test of the AgentBridge SIP bridge.
//
//  Launches the real agent.exe with SIP enabled, then acts as a SIP softphone
//  (SIPSorcery UAC) on loopback and asserts the whole surface of the bridge:
//    1. correct PIN (DTMF) → the call moves to the conversation phase;
//    2. a partially typed PIN does not concatenate across calls (buffer reset);
//    3. DTMF during the conversation → ignored, no STT garbage;
//    4. re-INVITE (hold) from the client → the call survives the renegotiation;
//    5. a second incoming call while one is active → rejected (486 Busy);
//    6. outgoing call to a rejecting remote → reported failed, state cleaned;
//    7. outgoing call to an answering remote → conversation + /v1/sip/hangup;
//    8. invalid destination → 400;
//    9. /v1/sip/answer toggle: gate off → 486, back on → PIN phase;
//   10. wrong PIN then correct in the same call (counter reset) + overflow digit;
//   11. three wrong PINs → the server hangs up and arms the 24 h lockout;
//   12. while locked → the call is answered with a spoken notice and hung up;
//   13. the lockout survives a server restart;
//   14. an expired lockout (locked_until in the past) lets calls through again;
//   15. allow-list mode: listed caller skips the PIN, unknown caller gets it;
//   16. answer mode "none": straight to the conversation;
//   17. empty PIN config → calls are rejected, the gate never opens;
//   18. full voice loop ×2 → RTP speech is transcribed (whisper), the agent replies
//       (DeepSeekBridge) and the TTS reply comes back over RTP (runs last on its own
//       server: it depends on the LLM bridge, which can stall);
//   19. PIN attempts are cumulative across calls: 2 wrong + hangup + 1 more wrong
//       arms the lockout (the counter survives redials).
//
//  Usage:
//    dotnet run --project e2e\SipSmoke [path-to-agent.exe] [base-url] [sip-port]
//    dotnet run --project e2e\SipSmoke -- --skip-voice [path-to-agent.exe] ...   # no LLM needed
//    dotnet run --project e2e\SipSmoke -- --voice-only [path-to-agent.exe] ...  # media-path iteration: test 18 only
//  Exit code 0 = all checks passed. Requires the whisper model in voiceagent-stt/
//  (first transcribe downloads it) and a working default LLM provider for test 18.
// ═══════════════════════════════════════════════════════════════════════
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

internal static class Program
{
    private const string Pin = "12345";
    private const string WrongPin = "99999";
    private static int _pass, _fail;
    private static int _serverPid;
    private static string _agentLogDir = "";

    private static void Check(string name, bool cond)
    {
        if (cond) { _pass++; Console.WriteLine($"  OK   {name}"); }
        else { _fail++; Console.WriteLine($"  FAIL {name}"); }
    }

    private static async Task<int> Main(string[] args)
    {
        var skipVoice = args.Contains("--skip-voice");
        var voiceOnly = args.Contains("--voice-only");   // diagnostic mode: run only the voice loop (test 18)
        var pos = args.Where(a => !a.StartsWith('-')).ToArray();   // flags excluded from the positional args
        var baseDir = AppContext.BaseDirectory;
        var agentExe = pos.Length > 0 ? Path.GetFullPath(pos[0])
            : Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "bin", "Debug", "net10.0", "agent.exe"));
        var baseUrl = pos.Length > 1 ? pos[1] : "http://localhost:5390";
        var sipPort = pos.Length > 2 ? int.Parse(pos[2]) : 5070;
        var uacPort = sipPort + 1;
        _agentLogDir = Path.Combine(Path.GetDirectoryName(agentExe)!, "logs");

        if (!File.Exists(agentExe))
        {
            Console.WriteLine($"agent.exe not found at {agentExe}");
            return 2;
        }

        // Fresh state: a leftover lockout from a previous run would skew the tests.
        var statePath = StatePath();
        if (File.Exists(statePath)) File.Delete(statePath);

        if (voiceOnly) goto VoiceLoop;

        // First server run (pin mode): tests 1-12. The lockout armed by test 11 is what the
        // restart tests (13-14) verify against.
        using (var server = await LaunchAsync(agentExe, baseUrl, sipPort,
            $"--Sip:Enabled true --Sip:ListenPort {sipPort} --Sip:Pin {Pin} --Sip:MaxPinAttempts 3 --Sip:Lang en"))
        {
        // ── Status shape: STT/TTS availability flags ──
        var st = await SipStatus(baseUrl);
        var sttFlag = st?.TryGetProperty("stt_available", out var stv) == true
            && (stv.ValueKind == JsonValueKind.True || stv.ValueKind == JsonValueKind.False);
        var ttsFlag = st?.TryGetProperty("tts_available", out var ttv) == true
            && (ttv.ValueKind == JsonValueKind.True || ttv.ValueKind == JsonValueKind.False);
        Check("status exposes stt/tts availability flags", sttFlag && ttsFlag);

        // ── Config surface: GET /v1/sip/config (masked), set (live), bad input ──
        // The set endpoint persists the "Sip" section to the executable's appsettings.json —
        // back it up and restore it so the developer's local config is never left modified.
        Console.WriteLine("Config: /v1/sip/config read/write surface");
        var appsettingsPath = Path.Combine(Path.GetDirectoryName(agentExe)!, "appsettings.json");
        var appsettingsBackup = File.Exists(appsettingsPath) ? File.ReadAllText(appsettingsPath) : null;
        try
        {
            async Task<(bool Ok, bool Restart, string Body)> SetCfg(string key, string value)
            {
                using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(5) };
                var body = JsonSerializer.Serialize(new { key, value });
                using var resp = await http.PostAsync("/v1/sip/config", new StringContent(body, Encoding.UTF8, "application/json"));
                var text = await resp.Content.ReadAsStringAsync();
                var restart = false;
                if (resp.IsSuccessStatusCode)
                    using (var doc = JsonDocument.Parse(text))
                        restart = doc.RootElement.TryGetProperty("restart_required", out var r) && r.GetBoolean();
                return (resp.IsSuccessStatusCode, restart, text);
            }

            using (var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(5) })
            {
                using var resp = await http.GetAsync("/v1/sip/config");
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var sip = doc.RootElement.GetProperty("sip");
                Check("config exposes the effective keys with secrets masked",
                    resp.IsSuccessStatusCode &&
                    sip.TryGetProperty("pin_set", out var ps) && ps.ValueKind == JsonValueKind.True &&
                    sip.TryGetProperty("answer_mode", out var am) && am.GetString() == "pin" &&
                    !sip.TryGetProperty("pin", out _) && !sip.TryGetProperty("password", out _));
            }

            var setNone = await SetCfg("AnswerMode", "none");
            Check("config set applies live (answer_mode → none, no restart)",
                setNone.Ok && !setNone.Restart &&
                (await SipStatus(baseUrl))?.GetProperty("answer_mode").GetString() == "none");
            var setBack = await SetCfg("AnswerMode", "pin");
            Check("config set restores pin mode", setBack.Ok && (await SipStatus(baseUrl))?.GetProperty("answer_mode").GetString() == "pin");
            Check("unknown config key refused (400)", !(await SetCfg("BogusKey", "x")).Ok);
            Check("invalid value refused (400)", !(await SetCfg("ListenPort", "not-a-port")).Ok);
        }
        finally
        {
            if (appsettingsBackup != null) File.WriteAllText(appsettingsPath, appsettingsBackup);
        }

        // ── Test 1: correct PIN → conversation ──
        Console.WriteLine("Test 1: correct PIN connects to the conversation phase");
        using (var client = new CallClient(uacPort))
        {
            if (await client.CallAsync($"sip:agent@127.0.0.1:{sipPort}", 20_000))
            {
                await EnterPin(client, Pin);
                var conv = await WaitForAsync(async () =>
                    (await SipStatus(baseUrl))?.GetProperty("phase").GetString() == "conversation", 15_000);
                Check("correct PIN reaches conversation phase", conv);
                await client.HangupAsync();
            }
            else
            {
                Check("correct PIN reaches conversation phase", false);
            }
        }

        // ── Test 2: a partially typed PIN must not concatenate across calls ──
        await WaitForIdleAsync(baseUrl);
        Console.WriteLine("Test 2: partial PIN does not concatenate across calls");
        using (var first = new CallClient(uacPort + 1))
        {
            var answered = await first.CallAsync($"sip:agent@127.0.0.1:{sipPort}", 20_000);
            if (!answered) { Check("partial PIN keeps the call in the pin phase", false); }
            else
            {
                // Type only "12" of "12345", then hang up.
                for (int i = 0; i < 2; i++) { await first.Ua.SendDtmf((byte)(Pin[i] - '0')); await Task.Delay(250); }
                var stillPin = (await SipStatus(baseUrl))?.GetProperty("phase").GetString() == "pin";
                Check("partial PIN keeps the call in the pin phase", stillPin);
                await first.HangupAsync();
            }
        }
        await WaitForIdleAsync(baseUrl);
        using (var second = new CallClient(uacPort + 2))
        {
            var answered = await second.CallAsync($"sip:agent@127.0.0.1:{sipPort}", 20_000);
            if (!answered) { Check("PIN buffer is reset between calls", false); }
            else
            {
                // If the buffer leaked, typing the remaining "345" would complete "12345" → conversation.
                await EnterPin(second, Pin.Substring(2));
                var phase = (await SipStatus(baseUrl))?.GetProperty("phase").GetString();
                Check("PIN buffer is reset between calls", phase == "pin");   // 3 digits < 5: incomplete
                await second.HangupAsync();
            }
        }

        // ── Test 3: DTMF during the conversation → ignored, no STT garbage ──
        await WaitForIdleAsync(baseUrl);
        Console.WriteLine("Test 3: DTMF during conversation does not reach the STT");
        using (var client = new CallClient(uacPort + 3))
        {
            var answered = await client.CallAsync($"sip:agent@127.0.0.1:{sipPort}", 20_000);
            if (!answered) { Check("DTMF ignored during conversation", false); }
            else
            {
                await EnterPin(client, Pin);
                var conv = await WaitForAsync(async () =>
                    (await SipStatus(baseUrl))?.GetProperty("phase").GetString() == "conversation", 15_000);
                if (!conv) { Check("DTMF ignored during conversation", false); }
                else
                {
                    await WaitSpeechSilenceAsync(client, 2_000, 60_000);
                    var before = CountLog("SIP caller said");
                    for (int i = 0; i < 5; i++) { await client.Ua.SendDtmf((byte)(i + 1)); await Task.Delay(200); }
                    await Task.Delay(6_000);
                    var still = (await SipStatus(baseUrl))?.GetProperty("phase").GetString() == "conversation";
                    Check("DTMF ignored during conversation", still && CountLog("SIP caller said") == before);
                }
                await client.HangupAsync();
            }
        }

        // ── Test 4: re-INVITE (hold) from the client → call survives ──
        await WaitForIdleAsync(baseUrl);
        Console.WriteLine("Test 4: hold re-INVITE does not drop the call");
        using (var client = new CallClient(uacPort + 4))
        {
            var answered = await client.CallAsync($"sip:agent@127.0.0.1:{sipPort}", 20_000);
            if (!answered) { Console.WriteLine($"  (server response: {client.LastResponse ?? "none"})"); Check("hold re-INVITE survives", false); }
            else
            {
                await EnterPin(client, Pin);
                var conv = await WaitForAsync(async () =>
                    (await SipStatus(baseUrl))?.GetProperty("phase").GetString() == "conversation", 15_000);
                if (!conv) { Console.WriteLine($"  (never reached conversation; response {client.LastResponse})"); Check("hold re-INVITE survives", false); }
                else
                {
                    client.Ua.PutOnHold();
                    await Task.Delay(3_000);
                    var afterHold = (await SipStatus(baseUrl))?.GetProperty("phase").GetString() == "conversation";
                    client.Ua.TakeOffHold();
                    await Task.Delay(3_000);
                    var afterUnhold = (await SipStatus(baseUrl))?.GetProperty("phase").GetString() == "conversation";
                    Check("hold re-INVITE survives", afterHold && afterUnhold);
                }
                await client.HangupAsync();
            }
        }

        // ── Test 5: a second call while one is active → 486 Busy ──
        await WaitForIdleAsync(baseUrl);
        Console.WriteLine("Test 5: second incoming call is rejected while busy");
        using (var a = new CallClient(uacPort + 5))
        {
            var ok = await a.CallAsync($"sip:agent@127.0.0.1:{sipPort}", 20_000);
            if (!ok)
            {
                // The client-side answer reporting can race a preceding call's teardown: retry once.
                Console.WriteLine($"  (call A first attempt: {a.LastResponse ?? "none"} — retrying)");
                ok = await a.CallAsync($"sip:agent@127.0.0.1:{sipPort}", 20_000);
            }
            if (!ok) { Console.WriteLine($"  (call A not answered: {a.LastResponse ?? "none"})"); Check("second call rejected while busy (486)", false); }
            else
            {
                await EnterPin(a, Pin);
                var conv = await WaitForAsync(async () =>
                    (await SipStatus(baseUrl))?.GetProperty("phase").GetString() == "conversation", 15_000);
                using var b = new CallClient(uacPort + 6);
                var answeredB = await b.CallAsync($"sip:agent@127.0.0.1:{sipPort}", 10_000);
                Console.WriteLine($"  (call B response: {b.LastResponse ?? "none"})");
                // A rejected call surfaces as ClientCallFailed with the 486 reason phrase ("BusyHere").
                Check("second call rejected while busy (486)", !answeredB && (b.LastResponse?.Contains("Busy") ?? false));
                await a.HangupAsync();
            }
        }

        // ── Test 6: outgoing call to a rejecting remote → failed + cleaned ──
        await WaitForIdleAsync(baseUrl);
        Console.WriteLine("Test 6: outgoing call rejected by the remote is cleaned up");
        using (var remote = new AnswerServer(uacPort + 70, reject: true))
        using (var http = new HttpClient { BaseAddress = new Uri(baseUrl) })
        {
            var body = JsonSerializer.Serialize(new { uri = $"sip:agent@127.0.0.1:{uacPort + 70}" });
            var r = await http.PostAsync("/v1/sip/call", new StringContent(body, Encoding.UTF8, "application/json"));
            Check("outbound rejection reported as failure", !r.IsSuccessStatusCode);
            var idle = await WaitForAsync(async () =>
                (await SipStatus(baseUrl))?.GetProperty("call_active").GetBoolean() == false, 10_000);
            Check("no stale call after rejected outbound", idle);
        }

        // ── Test 7: outgoing call to an answering remote → conversation + /v1/sip/hangup ──
        Console.WriteLine("Test 7: outgoing call to an answering remote + /v1/sip/hangup");
        using (var remote = new AnswerServer(uacPort + 71))
        using (var http = new HttpClient { BaseAddress = new Uri(baseUrl) })
        {
            var body = JsonSerializer.Serialize(new { uri = $"sip:agent@127.0.0.1:{uacPort + 71}" });
            var r = await http.PostAsync("/v1/sip/call", new StringContent(body, Encoding.UTF8, "application/json"));
            var conv = r.IsSuccessStatusCode && await WaitForAsync(async () =>
                (await SipStatus(baseUrl))?.GetProperty("phase").GetString() == "conversation", 15_000);
            Check("outgoing call to an answering remote reaches conversation", conv);
            var hang = await http.PostAsync("/v1/sip/hangup", new StringContent("{}", Encoding.UTF8, "application/json"));
            var idle = hang.IsSuccessStatusCode && await WaitForAsync(async () =>
                (await SipStatus(baseUrl))?.GetProperty("call_active").GetBoolean() == false, 10_000);
            Check("/v1/sip/hangup ends the active call", idle);
        }

        // ── Test 8: invalid destination → 400 ──
        Console.WriteLine("Test 8: invalid destination is refused");
        using (var http = new HttpClient { BaseAddress = new Uri(baseUrl) })
        {
            var body = JsonSerializer.Serialize(new { uri = "not a sip uri" });
            var r = await http.PostAsync("/v1/sip/call", new StringContent(body, Encoding.UTF8, "application/json"));
            Check("invalid destination refused (400)", r.StatusCode == HttpStatusCode.BadRequest);
        }

        // ── Test 9: /v1/sip/answer toggle — gate off → 486, back on → PIN phase ──
        Console.WriteLine("Test 9: answer gate toggle (off → 486, on → PIN phase)");
        using (var http = new HttpClient { BaseAddress = new Uri(baseUrl) })
        {
            var off = await http.PostAsync("/v1/sip/answer", new StringContent("{\"on\":false}", Encoding.UTF8, "application/json"));
            using (var client = new CallClient(uacPort + 90))
            {
                var answered = await client.CallAsync($"sip:agent@127.0.0.1:{sipPort}", 10_000);
                Check("call rejected while the answer gate is off", off.IsSuccessStatusCode
                    && !answered && (client.LastResponse?.Contains("Busy") ?? false));
            }
            var on = await http.PostAsync("/v1/sip/answer", new StringContent("{\"on\":true}", Encoding.UTF8, "application/json"));
            using (var client = new CallClient(uacPort + 91))
            {
                var answered = await client.CallAsync($"sip:agent@127.0.0.1:{sipPort}", 20_000);
                if (!answered) { Check("answer gate back on accepts calls again", false); }
                else
                {
                    var pinPhase = await WaitForAsync(async () =>
                        (await SipStatus(baseUrl))?.GetProperty("phase").GetString() == "pin", 15_000);
                    Check("answer gate back on accepts calls again", on.IsSuccessStatusCode && pinPhase);
                    await client.HangupAsync();
                }
            }
        }

        // ── Test 10: wrong PIN then correct in the same call + overflow digit ──
        await WaitForIdleAsync(baseUrl);
        Console.WriteLine("Test 10: wrong PIN then correct in the same call, + overflow digit");
        using (var client = new CallClient(uacPort + 100))
        {
            var answered = await client.CallAsync($"sip:agent@127.0.0.1:{sipPort}", 20_000);
            if (!answered) { Check("recovery from a wrong PIN in the same call", false); }
            else
            {
                await EnterPin(client, WrongPin);
                await Task.Delay(1_500);   // let the "attempts left" announcement play
                await EnterPin(client, Pin);
                var conv = await WaitForAsync(async () =>
                    (await SipStatus(baseUrl))?.GetProperty("phase").GetString() == "conversation", 15_000);
                var remaining = (await SipStatus(baseUrl))?.GetProperty("pin_remaining").GetInt32();
                Check("recovery from a wrong PIN in the same call", conv && remaining == 3);   // counter reset on success
                await client.HangupAsync();
            }
        }
        await WaitForIdleAsync(baseUrl);
        using (var client2 = new CallClient(uacPort + 101))
        {
            var answered = await client2.CallAsync($"sip:agent@127.0.0.1:{sipPort}", 20_000);
            if (!answered) { Check("overflow digit does not disrupt the PIN acceptance", false); }
            else
            {
                await EnterPin(client2, Pin + "0");   // 12345 + one extra digit
                var conv = await WaitForAsync(async () =>
                    (await SipStatus(baseUrl))?.GetProperty("phase").GetString() == "conversation", 15_000);
                Check("overflow digit does not disrupt the PIN acceptance", conv);
                await client2.HangupAsync();
            }
        }

        // ── Test 11: three wrong PINs → hangup + lockout ──
        await WaitForIdleAsync(baseUrl);
        Console.WriteLine("Test 11: three wrong PINs hang up and arm the lockout");
        using (var client = new CallClient(uacPort + 10))
        {
            if (await client.CallAsync($"sip:agent@127.0.0.1:{sipPort}", 20_000))
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    for (int i = 0; i < WrongPin.Length; i++)
                    {
                        await client.Ua.SendDtmf((byte)(WrongPin[i] - '0'));
                        await Task.Delay(200);
                    }
                    await Task.Delay(800);   // let the server announce the mistake
                }
                var ended = await WaitForAsync(async () =>
                    client.HungUp || (await SipStatus(baseUrl))?.GetProperty("call_active").GetBoolean() == false, 15_000);
                Check("server hangs up after 3 wrong PINs", ended);
            }
            else
            {
                Check("server hangs up after 3 wrong PINs", false);
            }
        }
        var locked = await WaitForAsync(async () =>
            (await SipStatus(baseUrl))?.TryGetProperty("locked_until", out var v) == true
            && v.ValueKind == JsonValueKind.String && v.GetString()?.Length > 0, 10_000);
        Check("lockout state armed (locked_until set)", locked);

        // ── Test 12: while locked the call is answered then hung up ──
        Console.WriteLine("Test 12: locked state answers with a notice then hangs up");
        using (var client = new CallClient(uacPort + 20))
        {
            var answered = await client.CallAsync($"sip:agent@127.0.0.1:{sipPort}", 20_000);
            if (answered)
            {
                var ended = await WaitForAsync(() => Task.FromResult(client.HungUp), 15_000);
                Check("locked call is hung up by the server", ended);
                var idle = await WaitForAsync(async () =>
                    (await SipStatus(baseUrl))?.GetProperty("call_active").GetBoolean() == false, 5_000);
                Check("no call left active after lockout", idle);
            }
            else
            {
                Console.WriteLine($"  (server response: {client.LastResponse ?? "none"})");
                Check("locked call is hung up by the server", false);
            }
        }

        }   // first server run (tests 1-9) — killed on dispose

        // The first server run is done: the lockout is persisted to disk.
        Console.WriteLine("  (server restarting for the lockout tests)");

        // ── Test 13: the lockout survives a server restart ──
        Console.WriteLine("Test 13: lockout survives a restart");
        using (var server = await LaunchAsync(agentExe, baseUrl, sipPort,
            $"--Sip:Enabled true --Sip:ListenPort {sipPort} --Sip:Pin {Pin} --Sip:MaxPinAttempts 3 --Sip:Lang en"))
        using (var client = new CallClient(uacPort + 30))
        {
            var answered = await client.CallAsync($"sip:agent@127.0.0.1:{sipPort}", 20_000);
            if (!answered) { Console.WriteLine($"  (server response: {client.LastResponse ?? "none"})"); Check("lockout survives a restart", false); }
            else
            {
                var ended = await WaitForAsync(() => Task.FromResult(client.HungUp), 15_000);
                var stillLocked = (await SipStatus(baseUrl))?.TryGetProperty("locked_until", out var v) == true
                    && v.ValueKind == JsonValueKind.String && v.GetString()?.Length > 0;
                Check("lockout survives a restart", ended && stillLocked);
            }
        }

        // ── Test 14: an expired lockout lets calls through again ──
        Console.WriteLine("Test 14: expired lockout re-enables the PIN gate");
        WriteLockoutState(DateTime.UtcNow.AddHours(-1));
        using (var server = await LaunchAsync(agentExe, baseUrl, sipPort,
            $"--Sip:Enabled true --Sip:ListenPort {sipPort} --Sip:Pin {Pin} --Sip:MaxPinAttempts 3 --Sip:Lang en"))
        using (var client = new CallClient(uacPort + 31))
        {
            var answered = await client.CallAsync($"sip:agent@127.0.0.1:{sipPort}", 20_000);
            if (!answered) { Check("expired lockout re-enables the PIN gate", false); }
            else
            {
                var pinPhase = await WaitForAsync(async () =>
                    (await SipStatus(baseUrl))?.GetProperty("phase").GetString() == "pin", 15_000);
                Check("expired lockout re-enables the PIN gate", pinPhase);
                await EnterPin(client, Pin);
                var conv = await WaitForAsync(async () =>
                    (await SipStatus(baseUrl))?.GetProperty("phase").GetString() == "conversation", 15_000);
                Check("correct PIN works after lockout expiry", conv);
                await client.HangupAsync();
            }
        }

        // ── Test 15: allow-list mode skips the PIN for listed callers ──
        Console.WriteLine("Test 15: allow-list mode skips the PIN for listed callers");
        using (var server = await LaunchAsync(agentExe, baseUrl, sipPort,
            $"--Sip:Enabled true --Sip:ListenPort {sipPort} --Sip:Pin {Pin} --Sip:AnswerMode allowlist --Sip:AllowedCallers:0 thisis --Sip:Lang en"))
        {
            using (var listed = new CallClient(uacPort + 40))
            {
                var answered = await listed.CallAsync($"sip:agent@127.0.0.1:{sipPort}", 20_000);
                if (!answered) { Check("listed caller skips the PIN", false); }
                else
                {
                    var conv = await WaitForAsync(async () =>
                        (await SipStatus(baseUrl))?.GetProperty("phase").GetString() == "conversation", 15_000);
                    Check("listed caller skips the PIN", conv);
                    await listed.HangupAsync();
                }
            }
            using (var stranger = new CallClient(uacPort + 41))
            {
                await WaitForIdleAsync(baseUrl);   // the listed caller's hangup may still be tearing down
                var answered = await stranger.CallAsync($"sip:agent@127.0.0.1:{sipPort}", 20_000, username: "stranger");
                if (!answered) { Check("unknown caller falls back to the PIN", false); }
                else
                {
                    var pinPhase = await WaitForAsync(async () =>
                        (await SipStatus(baseUrl))?.GetProperty("phase").GetString() == "pin", 15_000);
                    Check("unknown caller falls back to the PIN", pinPhase);
                    await stranger.HangupAsync();
                }
            }
        }

        // ── Test 16: answer mode "none" goes straight to the conversation ──
        Console.WriteLine("Test 16: answer mode 'none' connects without a PIN");
        using (var server = await LaunchAsync(agentExe, baseUrl, sipPort,
            $"--Sip:Enabled true --Sip:ListenPort {sipPort} --Sip:Pin {Pin} --Sip:AnswerMode none --Sip:Lang en"))
        using (var client = new CallClient(uacPort + 50))
        {
            var answered = await client.CallAsync($"sip:agent@127.0.0.1:{sipPort}", 20_000);
            if (!answered) { Check("mode none connects without a PIN", false); }
            else
            {
                var conv = await WaitForAsync(async () =>
                    (await SipStatus(baseUrl))?.GetProperty("phase").GetString() == "conversation", 15_000);
                Check("mode none connects without a PIN", conv);
                await client.HangupAsync();
            }
        }

        // ── Test 17: empty PIN config → calls are rejected, the gate never opens ──
        Console.WriteLine("Test 17: empty PIN config rejects calls");
        using (var server = await LaunchAsync(agentExe, baseUrl, sipPort,
            $"--Sip:Enabled true --Sip:ListenPort {sipPort} --Sip:Pin= --Sip:MaxPinAttempts 3 --Sip:Lang en"))
        using (var client = new CallClient(uacPort + 110))
        {
            var answered = await client.CallAsync($"sip:agent@127.0.0.1:{sipPort}", 20_000);
            if (!answered) { Check("no PIN configured → call is rejected", false); }
            else
            {
                var ended = await WaitForAsync(() => Task.FromResult(client.HungUp), 15_000);
                var idle = await WaitForAsync(async () =>
                    (await SipStatus(baseUrl))?.GetProperty("call_active").GetBoolean() == false, 5_000);
                Check("no PIN configured → call is rejected", ended && idle);
            }
        }

        // ── Test 18: full voice loop — RTP speech → STT → agent → TTS → RTP, twice ──
        // On its OWN server: test 11 arms the 24 h lockout and test 14 expires it afterwards, so
        // this fresh server reads an expired lockout state and the PIN gate is open. The loop
        // depends on the LLM bridge (can stall / produce very long replies), so it runs last.
        // Pass --skip-voice to skip it (fast CI runs without an LLM backend).
        VoiceLoop:
        if (voiceOnly) WriteLockoutState(DateTime.UtcNow.AddHours(-1));   // voice-only mode: gate open from the start
        if (!skipVoice)
        {
            Console.WriteLine("Test 18: full voice loop (RTP speech → STT → agent → TTS → RTP, ×2)");
            using (var server = await LaunchAsync(agentExe, baseUrl, sipPort,
                $"--Sip:Enabled true --Sip:ListenPort {sipPort} --Sip:Pin {Pin} --Sip:MaxPinAttempts 3 --Sip:Lang en"))
            {
                byte[]? speechWav = null;
                using (var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(3) })
                {
                    var r = await http.PostAsync("/v1/audio/speech",
                        new StringContent("{\"input\":\"Hello, this is a voice loop test for the agent.\",\"lang\":\"en\"}", Encoding.UTF8, "application/json"));
                    if (r.IsSuccessStatusCode) speechWav = await r.Content.ReadAsByteArrayAsync();
                    else Console.WriteLine($"  (speech synthesis failed: {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()})");
                }
                if (speechWav == null)
                {
                    Check("voice loop transcribed to text", false);
                    Check("agent replied (DeepSeekBridge)", false);
                    Check("reply audio came back over RTP", false);
                }
                else
                {
                    var speechPcm = speechWav.AsSpan(44).ToArray();   // strip the WAV header
                    Console.WriteLine($"  (speech sample: {speechWav.Length} bytes)");
                    using (var client = new CallClient(uacPort + 2))
                    {
                        var answered = await client.CallAsync($"sip:agent@127.0.0.1:{sipPort}", 20_000);
                        if (!answered) { Console.WriteLine("  (call not answered)"); Check("voice loop transcribed to text", false); }
                        else
                        {
                            await EnterPin(client, Pin);
                            var conv = await WaitForAsync(async () =>
                                (await SipStatus(baseUrl))?.GetProperty("phase").GetString() == "conversation", 15_000);
                            if (!conv) { Console.WriteLine("  (never reached conversation)"); Check("voice loop transcribed to text", false); }
                            else
                            {
                                for (int turn = 1; turn <= 2; turn++)
                                {
                                    // Let the "code accepted" announcement (or the previous reply) play
                                    // out BEFORE the reset: the captured audio must reflect only the reply.
                                    await WaitSpeechSilenceAsync(client, 2_000, 30_000);
                                    client.ResetReceivedAudio();
                                    Console.WriteLine($"  (turn {turn}: sending speech)");
                                    await client.Media.AudioExtrasSource.SendAudioFromStream(new MemoryStream(speechPcm), AudioSamplingRatesEnum.Rate24kHz);

                                    var transcribed = await WaitForAsync(() => Task.FromResult(CountLog("SIP caller said") >= turn), 60_000);
                                    if (turn == 1) Check("voice loop transcribed to text", transcribed);
                                    else Check("second utterance transcribed too", transcribed);
                                    var replied = await WaitForAsync(() => Task.FromResult(CountLog("SIP agent replied") >= turn), 180_000);
                                    if (turn == 1) Check("agent replied (DeepSeekBridge)", replied);
                                    else Check("second agent reply produced", replied);

                                    // Let the TTS reply finish streaming before the next utterance:
                                    // capture pauses while the reply plays, so a sample sent earlier
                                    // would be dropped (barge-in is intentionally disabled).
                                    await WaitSpeechSilenceAsync(client, 3_000, 120_000);

                                    var audioOk = client.ReceivedAudioMs > 500 && client.ReceivedAudioRms > 0.02;
                                    Console.WriteLine($"  (reply audio: {client.ReceivedAudioMs} ms, peak rms {client.ReceivedAudioRms:F3})");
                                    if (turn == 1) Check("reply audio came back over RTP", audioOk);
                                }
                            }
                            await client.HangupAsync();
                        }
                    }
                }
            }
        }

        // ── Test 19: PIN attempts are cumulative across calls ──
        // Two wrong PINs in one call, hang up, redial, one more wrong → 3 total → lockout.
        // (A redial must not buy a fresh attempt budget — see PinAuthGate.ResetBuffer.)
        if (!voiceOnly)
        {
            Console.WriteLine("Test 19: PIN attempts are cumulative across calls");
        using (var server = await LaunchAsync(agentExe, baseUrl, sipPort,
            $"--Sip:Enabled true --Sip:ListenPort {sipPort} --Sip:Pin {Pin} --Sip:MaxPinAttempts 3 --Sip:Lang en"))
        {
            using (var a = new CallClient(uacPort + 80))
            {
                var answered = await a.CallAsync($"sip:agent@127.0.0.1:{sipPort}", 20_000);
                if (!answered) { Check("attempt counter survives hangup and redial", false); }
                else
                {
                    for (int attempt = 0; attempt < 2; attempt++)
                    {
                        for (int i = 0; i < WrongPin.Length; i++)
                        {
                            await a.Ua.SendDtmf((byte)(WrongPin[i] - '0'));
                            await Task.Delay(200);
                        }
                        await Task.Delay(800);
                    }
                    var active = (await SipStatus(baseUrl))?.GetProperty("call_active").GetBoolean() == true;
                    // The second wrong-PIN announcement may still be playing when the burst ends;
                    // wait until the counter is actually visible in the status before asserting.
                    var remaining = await WaitForAsync(async () =>
                        (await SipStatus(baseUrl))?.GetProperty("pin_remaining").GetInt32() == 1, 10_000);
                    Check("two wrong attempts leave one attempt left", active && remaining);
                    await a.HangupAsync();
                }
            }
            await WaitForIdleAsync(baseUrl);
            using (var b = new CallClient(uacPort + 81))
            {
                var answered = await b.CallAsync($"sip:agent@127.0.0.1:{sipPort}", 20_000);
                if (!answered) { Check("third wrong attempt across calls arms the lockout", false); }
                else
                {
                    await EnterPin(b, WrongPin);
                    var ended = await WaitForAsync(async () =>
                        b.HungUp || (await SipStatus(baseUrl))?.GetProperty("call_active").GetBoolean() == false, 15_000);
                    var locked = await WaitForAsync(async () =>
                        (await SipStatus(baseUrl))?.TryGetProperty("locked_until", out var v) == true
                        && v.ValueKind == JsonValueKind.String && v.GetString()?.Length > 0, 10_000);
                    Check("third wrong attempt across calls arms the lockout", ended && locked);
                }
            }
        }
        }

        Console.WriteLine(_fail == 0 ? "\nALL SIP SMOKE CHECKS PASSED" : $"\n{_fail} CHECK(S) FAILED");
        if (_fail > 0) DumpLogTail();
        return _fail == 0 ? 0 : 1;
    }

    // ── Server lifecycle ────────────────────────────────────────────────

    private static string StatePath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agent", "sipstate.json");

    private static void WriteLockoutState(DateTime lockedUntil)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath())!);
        File.WriteAllText(StatePath(), JsonSerializer.Serialize(new { locked_until = lockedUntil.ToString("O") }));
    }

    /// <summary>Owns the launched agent process: Dispose kills it (a bare Process.Dispose
    /// would only release the handle and leave the server occupying the SIP port).</summary>
    private sealed class ServerHandle : IDisposable
    {
        private readonly Process _process;

        public ServerHandle(Process process) => _process = process;

        public void Dispose()
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
            _process.Dispose();
        }
    }

    private static async Task<ServerHandle> LaunchAsync(string agentExe, string baseUrl, int sipPort, string sipArgs)
    {
        var server = Process.Start(new ProcessStartInfo
        {
            FileName = agentExe,
            Arguments = $"--headless --no-update --enable-log --SkipIndexingOnStartup true --Urls {baseUrl} {sipArgs}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        server.OutputDataReceived += (_, e) => Console.WriteLine($"[agent] {e.Data}");
        server.BeginOutputReadLine();
        server.BeginErrorReadLine();
        _serverPid = server.Id;

        if (!await WaitForAsync(async () => (await SipStatus(baseUrl))?.GetProperty("listening").GetBoolean() == true, 30_000))
        {
            Console.WriteLine("agent SIP server never became ready");
            try { server.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException("SIP server did not start");
        }
        Console.WriteLine($"agent SIP server listening (pid {server.Id})");
        return new ServerHandle(server);
    }

    private static void DumpLogTail()
    {
        var log = Directory.Exists(_agentLogDir)
            ? Directory.GetFiles(_agentLogDir, "*.txt").OrderByDescending(File.GetLastWriteTime).FirstOrDefault()
            : null;
        if (log != null)
        {
            Console.WriteLine($"\n--- server log tail ({log}) ---");
            Console.WriteLine(string.Join('\n', File.ReadAllLines(log).TakeLast(80)));
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>Waits until the server reports no active call. A client hangup and the next
    /// test's call would otherwise race the server-side teardown (the INVITE arrives while the
    /// previous call is still being ended and correctly gets 486). SIPSorcery intermittently
    /// misses the BYE processing; the server then recovers on its RTP-inactivity timeout, so
    /// the wait is generous.</summary>
    private static async Task WaitForIdleAsync(string baseUrl, int timeoutMs = 45_000)
    {
        await WaitForAsync(async () =>
            (await SipStatus(baseUrl))?.GetProperty("call_active").GetBoolean() == false, timeoutMs);
    }

    private static async Task EnterPin(CallClient client, string pin)
    {
        for (int i = 0; i < pin.Length; i++)
        {
            await client.Ua.SendDtmf((byte)(pin[i] - '0'));
            await Task.Delay(250);
        }
    }

    /// <summary>Waits until the reply TTS has played out: no speech-level packet for
    /// <paramref name="silenceMs"/> (the server sends silence keepalive packets continuously,
    /// so a stream-length based wait would never end). Capped by <paramref name="timeoutMs"/>.</summary>
    private static async Task WaitSpeechSilenceAsync(CallClient client, int silenceMs, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var silentFor = (DateTime.UtcNow - client.LastSpeechUtc).TotalMilliseconds;
            if (client.ReceivedAudioRms > 0.02 && silentFor >= silenceMs) return;
            await Task.Delay(500);
        }
    }

    private static int CountLog(string marker)
    {
        try
        {
            var path = Path.Combine(_agentLogDir, $"{_serverPid}.txt");
            return File.Exists(path) ? File.ReadAllText(path).Split(marker).Length - 1 : 0;
        }
        catch { return 0; }
    }

    private static async Task<bool> WaitForAsync(Func<Task<bool>> predicate, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try { if (await predicate()) return true; } catch { }
            await Task.Delay(250);
        }
        return false;
    }

    private static async Task<JsonElement?> SipStatus(string baseUrl)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(3) };
        using var resp = await http.GetAsync("/v1/sip/status");
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("sip").Clone();
    }

    /// <summary>A VoIPMediaSession that offers only G.711 and emits no audio on its own: the
    /// parameterless ctor enables the on-hold music generator, which would stream music to the
    /// peer for the whole call.</summary>
    private static VoIPMediaSession NoMusicMediaSession()
    {
        var endpoint = new MediaEndPoints { AudioSource = new AudioExtrasSource() };
        var media = new VoIPMediaSession(new VoIPMediaSessionConfig { MediaEndPoint = endpoint });
        endpoint.AudioSource!.RestrictFormats(f => f.Codec == AudioCodecsEnum.PCMU || f.Codec == AudioCodecsEnum.PCMA);
        return media;
    }

    /// <summary>A minimal SIPSorcery softphone for the tests: places a call, sends DTMF,
    /// streams audio in and captures the audio the server sends back.</summary>
    private sealed class CallClient : IDisposable
    {
        private readonly SIPTransport _transport;
        private readonly VoIPMediaSession _media;
        private readonly TaskCompletionSource _answered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SIPUserAgent Ua { get; }
        public bool HungUp => _ended.Task.IsCompleted;
        public string? LastResponse { get; private set; }
        public VoIPMediaSession Media => _media;

        /// <summary>Milliseconds of decoded audio received from the server since the last reset.</summary>
        public int ReceivedAudioMs { get; private set; }

        /// <summary>Peak RMS of the decoded audio since the last reset (speech ≈ 0.02+; silence ≈ 0).</summary>
        public double ReceivedAudioRms { get; private set; }

        /// <summary>Moment the last speech-level packet arrived (UTC); default = last reset.</summary>
        public DateTime LastSpeechUtc { get; private set; }

        private readonly MemoryStream _receivedAudio = new();
        private readonly object _audioSync = new();

        public void ResetReceivedAudio()
        {
            lock (_audioSync)
            {
                _receivedAudio.SetLength(0);
                ReceivedAudioMs = 0;
                ReceivedAudioRms = 0;
                LastSpeechUtc = DateTime.UtcNow;
            }
        }

        public CallClient(int localPort)
        {
            _transport = new SIPTransport();
            _transport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, localPort)));
            // Config ctor WITHOUT the default Music source: the parameterless VoIPMediaSession
            // enables SetSource(Music) (on-hold music generator) and would stream hold music
            // to the server for the whole call, polluting the STT with "(upbeat music)".
            _media = NoMusicMediaSession();
            _media.AcceptRtpFromAny = true;
            // Capture the audio the server sends back: G.711 payloads only (DTMF events skipped).
            _media.OnRtpPacketReceived += (_, _, packet) =>
            {
                var payload = packet.Payload;
                if (payload == null || payload.Length == 0) return;
                var pt = packet.Header.PayloadType;
                if (pt != 0 && pt != 8) return;
                lock (_audioSync)
                {
                    long sumSquares = 0;
                    for (int i = 0; i < payload.Length; i++)
                    {
                        var sample = pt == 0
                            ? SIPSorcery.Media.MuLawDecoder.MuLawToLinearSample(payload[i])
                            : SIPSorcery.Media.ALawDecoder.ALawToLinearSample(payload[i]);
                        _receivedAudio.WriteByte((byte)(sample & 0xFF));
                        _receivedAudio.WriteByte((byte)((sample >> 8) & 0xFF));
                        sumSquares += (long)sample * sample;
                    }
                    var rms = Math.Sqrt(sumSquares / (double)payload.Length) / short.MaxValue;
                    if (rms > ReceivedAudioRms) ReceivedAudioRms = rms;
                    if (rms > 0.02) LastSpeechUtc = DateTime.UtcNow;
                    ReceivedAudioMs = (int)(_receivedAudio.Length / 2 / 8000.0 * 1000);
                }
            };
            Ua = new SIPUserAgent(_transport, null);
            Ua.ClientCallAnswered += (_, resp) =>
            {
                LastResponse = $"{(int)resp.Status} {resp.ReasonPhrase}";
                if (resp.Status == SIPResponseStatusCodesEnum.Ok) _answered.TrySetResult();
            };
            Ua.ClientCallFailed += (_, err, _) => { LastResponse = $"failed: {err}"; _answered.TrySetException(new InvalidOperationException("call failed")); };
            Ua.OnCallHungup += _ => _ended.TrySetResult();
        }

        /// <summary>Places a call. <paramref name="username"/> overrides the From header
        /// (used by the allow-list test to impersonate a different caller). No ring timeout:
        /// SIPSorcery's ring-timeout timer fires even after the call is answered and would
        /// cancel an established call; the caller-side WaitAsync enforces the deadline instead.</summary>
        public async Task<bool> CallAsync(string uri, int timeoutMs, string? username = null)
        {
            var callTask = Ua.Call(uri, username, null, _media, ringTimeout: 0);
            try
            {
                var result = await callTask.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs + 2000));
                if (!result) return false;
            }
            catch (TimeoutException)
            {
                LastResponse = "timeout";
                return false;
            }
            // _answered completes successfully only on 200 OK; a rejection (486/other) surfaces
            // as ClientCallFailed and must count as "not answered".
            return await Task.WhenAny(_answered.Task, Task.Delay(2000)) == _answered.Task
                && _answered.Task.IsCompletedSuccessfully;
        }

        public Task HangupAsync()
        {
            Ua.Hangup();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            try { Ua.Hangup(); } catch { }
            try { _media.Close(null); } catch { }
            _transport.Shutdown();
        }
    }

    /// <summary>A bare SIP UAS that answers every call (or rejects, with <paramref name="reject"/>).</summary>
    private sealed class AnswerServer : IDisposable
    {
        private readonly SIPTransport _transport;

        public AnswerServer(int localPort, bool reject = false)
        {
            _transport = new SIPTransport();
            _transport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, localPort)));
            var ua = new SIPUserAgent(_transport, null, true);
            ua.OnIncomingCall += async (_, req) =>
            {
                try
                {
                    if (reject)
                    {
                        var rj = ua.AcceptCall(req);
                        rj.Reject(SIPResponseStatusCodesEnum.BusyHere, "busy");
                        return;
                    }
                    var media = NoMusicMediaSession();
                    media.AcceptRtpFromAny = true;
                    var uas = ua.AcceptCall(req);
                    await ua.Answer(uas, media);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  (answer server error: {ex.Message})");
                }
            };
        }

        public void Dispose() => _transport.Shutdown();
    }
}
