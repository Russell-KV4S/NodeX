using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Text;
using NodeX.Core;

// NodeX live-node validation tool. Drives the real Core engine against the real AllStarLink network and
// writes a decoded packet trace so behaviour can be compared with PROJECT_SPEC.md section 5.
//
//   resolve <node>                         read-only: REST + DNS lookup, prints the raw REST reply
//   register                               HTTPS registration of YOUR node (env NODEX_NODE, NODEX_PASSWORD)
//   link <target> [--ip a.b.c.d[:port]] [--listen <sec>] [--key <sec>] [--dtmf <digits>] [--trace <file>]
//                                          dial <target> as IAX user "radio" from NODEX_NODE (no password)
//
// Nothing is transmitted (no KEY frame) unless --key is given. The password is read only from the
// NODEX_PASSWORD environment variable, never from arguments, and is never printed.

static string Env(string name) => Environment.GetEnvironmentVariable(name)?.Trim() ?? "";

if (args.Length == 0) { Usage(); return 2; }
string cmd = args[0].ToLowerInvariant();

try
{
    return cmd switch
    {
        "resolve" when args.Length >= 2 => await ResolveAsync(args[1]),
        "register" => await RegisterAsync(),
        "link" when args.Length >= 2 => await LinkAsync(args),
        "redial" when args.Length >= 2 => await RedialAsync(args[1]),
        "audio" => await AudioAsync(),
        "tone" => await ToneAsync(),
        "miclevels" => await MicLevelsAsync(),
        _ => Usage()
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine("FAILED: " + ex);
    return 1;
}

static int Usage()
{
    Console.WriteLine("usage: NodeX.LiveCheck resolve <node> | register | link <target> [--ip ip[:port]] [--listen s] [--key s] [--dtmf digits] [--trace file]");
    return 2;
}

static async Task<int> ResolveAsync(string node)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("NodeX-LiveCheck/1.0");

    string url = "https://stats.allstarlink.org/api/stats/" + Uri.EscapeDataString(node);
    Console.WriteLine($"GET {url}");
    try
    {
        using var resp = await http.GetAsync(url);
        string body = await resp.Content.ReadAsStringAsync();
        Console.WriteLine($"  -> HTTP {(int)resp.StatusCode}, {body.Length} bytes");
        Console.WriteLine("  raw body (first 1500 chars):");
        Console.WriteLine("  " + (body.Length > 1500 ? body[..1500] + "..." : body));
    }
    catch (Exception ex) { Console.WriteLine("  -> error: " + ex.Message); }

    foreach (string host in new[] { $"{node}.nodes.allstarlink.org", $"node{node}.allstarlink.org" })
    {
        Console.WriteLine($"DNS {host}");
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(host);
            Console.WriteLine("  -> " + string.Join(", ", addrs.Select(a => a.ToString())));
        }
        catch (Exception ex) { Console.WriteLine("  -> " + ex.Message); }
    }

    Console.WriteLine($"DnsPortLookup (SRV/TXT, raw UDP DNS): {(await DnsPortLookup.LookupAsync(node) is int dp ? dp.ToString() : "none")}");
    Console.WriteLine("NodeResolution (production code path):");
    var info = await new NodeResolution().ResolveNodeAsync(node);
    Console.WriteLine(info == null ? "  -> null (could not resolve)" : $"  -> {info.EndPoint} callsign='{info.Callsign}' desc='{info.Description}'");
    return info == null ? 1 : 0;
}

static async Task<int> RegisterAsync()
{
    string node = Env("NODEX_NODE"), pw = Env("NODEX_PASSWORD");
    if (node.Length == 0 || pw.Length == 0) { Console.Error.WriteLine("Set NODEX_NODE and NODEX_PASSWORD."); return 2; }

    await using var reg = new AslRegistrationService(new AslRegistrationConfig(node, pw));
    bool ok = await reg.SendHeartbeatAsync();
    Console.WriteLine($"registered={ok}  status='{reg.LastStatusMessage}'");
    Console.WriteLine($"perceived address={reg.PerceivedAddress}:{reg.PerceivedPort}  server refresh={reg.ServerRefresh}");
    return ok ? 0 : 1;
}

static async Task<int> LinkAsync(string[] a)
{
    string target = a[1];
    string node = Env("NODEX_NODE");
    if (node.Length == 0) { Console.Error.WriteLine("Set NODEX_NODE (your node number)."); return 2; }

    string? ipArg = null, tracePath = null, dtmf = null;
    int listen = 20, key = 0, mic = 0;
    bool newKey = false, linkList = false, noNewKey = false;
    for (int i = 2; i < a.Length; i++)
    {
        switch (a[i])
        {
            case "--newkey": newKey = true; break;
            case "--nonewkey": noNewKey = true; break;
            case "--linklist": linkList = true; break;
            case "--ip": ipArg = a[++i]; break;
            case "--listen": listen = int.Parse(a[++i]); break;
            case "--key": key = int.Parse(a[++i]); break;
            case "--mic": mic = int.Parse(a[++i]); break;
            case "--dtmf": dtmf = a[++i]; break;
            case "--trace": tracePath = a[++i]; break;
        }
    }
    tracePath ??= Path.Combine(Path.GetTempPath(), $"nodex-trace-{DateTime.Now:yyyyMMdd-HHmmss}.log");

    IPEndPoint? ep;
    if (ipArg != null)
    {
        var parts = ipArg.Split(':');
        ep = new IPEndPoint(IPAddress.Parse(parts[0]), parts.Length > 1 ? int.Parse(parts[1]) : Iax2Protocol.DefaultPort);
    }
    else
    {
        ep = (await new NodeResolution().ResolveNodeAsync(target))?.EndPoint;
    }
    if (ep == null) { Console.Error.WriteLine($"Could not resolve node {target}; pass --ip."); return 1; }

    var clock = Stopwatch.StartNew();
    using var trace = new StreamWriter(tracePath, false, Encoding.UTF8) { AutoFlush = true };
    void Log(string s)
    {
        string line = $"+{clock.Elapsed.TotalSeconds,7:F3}s {s}";
        lock (trace) { trace.WriteLine(line); Console.WriteLine(line); }
    }

    Log($"dial node {target} at {ep} as user 'radio', calling number {node}, trace file {tracePath}");

    await using var session = new Iax2Session
    {
        Username = "radio",
        LocalNodeNumber = node,
        RemoteNodeNumber = target,
        SendNewKeyOnAccept = !noNewKey
    };
    session.PacketTrace += (outbound, bytes) => Log((outbound ? "TX " : "RX ") + Decode(bytes.Span));
    session.StateChanged += s => Log($"STATE {s}");
    session.ErrorOccurred += e => Log($"ERROR {e}");
    session.NodeMessageReceived += m => Log($"TEXT '{m}'");
    session.RemoteKeyedChanged += k => Log($"REMOTE KEYED={k}");
    session.RoundTripTimeUpdated += r => Log($"RTT {r.TotalMilliseconds:F0} ms");
    int voiceFrames = 0, activeFrames = 0;
    bool rxActive = false;
    session.VoiceFrameReceived += (_, payload) =>
    {
        Interlocked.Increment(ref voiceFrames);
        // Silence frames are 0xFF; anything with real amplitude is audio (e.g. a spoken status announcement).
        int peak = 0;
        foreach (byte b in payload.Span) peak = Math.Max(peak, Math.Abs((int)NodeX.Core.G711Ulaw.DecodeSample(b)));
        bool active = peak > 400;                                     // about -38 dBFS
        if (active) Interlocked.Increment(ref activeFrames);
        if (active != rxActive) { rxActive = active; Log(active ? $"RX AUDIO ACTIVE (peak {peak})" : "RX AUDIO back to silence"); }
    };

    await session.ConnectAsync(ep);

    var connectDeadline = clock.Elapsed + TimeSpan.FromSeconds(15);
    while (session.State is Iax2SessionState.Connecting or Iax2SessionState.CallTokenHandshake && clock.Elapsed < connectDeadline)
        await Task.Delay(100);
    Log($"RESULT after handshake: state={session.State} reason={session.DisconnectReason}");
    if (session.State != Iax2SessionState.Connected) { Summary(session, voiceFrames, tracePath); return 1; }

    // Experiments for hubs that hang up after ~45 s: --newkey answers the hub's !NEWKEY1!, --linklist sends
    // an app_rpt "L " link-list text frame every 10 s like a real node does.
    if (newKey) { await session.SendTextAsync("!NEWKEY1!"); Log("sent !NEWKEY1!"); }
    if (linkList)
        _ = Task.Run(async () =>
        {
            while (session.State == Iax2SessionState.Connected)
            {
                await session.SendTextAsync("L ");
                Log("sent 'L '");
                await Task.Delay(10_000);
            }
        });

    if (dtmf != null)
    {
        foreach (char c in dtmf) { await session.SendDtmfAsync(c); await Task.Delay(120); }
        Log($"sent DTMF '{dtmf}'");
    }

    if (key > 0)
    {
        Log($"KEYING for {key}s (silence frames, 20 ms ulaw 0xFF)");
        await session.SetPttAsync(true);
        var silence = Enumerable.Repeat((byte)0xFF, 160).ToArray();
        var end = clock.Elapsed + TimeSpan.FromSeconds(key);
        var next = clock.Elapsed;
        while (clock.Elapsed < end && session.State == Iax2SessionState.Connected)
        {
            await session.SendAudioFrameAsync(silence);
            next += TimeSpan.FromMilliseconds(20);
            var wait = next - clock.Elapsed;
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
        }
        await session.SetPttAsync(false);
        Log("UNKEYED");
    }

    if (mic > 0)
    {
        // Live microphone -> G.711 -> link, keyed for <mic> seconds. Goes out on air via any linked RF node.
        await using var dev = new NodeX.UI.Desktop.Audio.NAudioDeviceService();
        dev.ErrorOccurred += e => Log("AUDIO DEVICE ERROR " + e);
        for (int c = 6; c > 0; c--) { Log($"MIC TX starts in {c} s - get ready to speak"); await Task.Delay(1000); }

        int sent = 0, peak = 0;
        Log($"KEYING for {mic}s with LIVE MIC - speak now");
        await session.SetPttAsync(true);
        dev.StartCapture(block =>
        {
            foreach (short s in block.Span) peak = Math.Max(peak, Math.Abs((int)s));
            _ = session.SendLinearPcmFrameAsync(block.Span);
            Interlocked.Increment(ref sent);
        });
        await Task.Delay(TimeSpan.FromSeconds(mic));
        dev.StopCapture();
        await session.SetPttAsync(false);
        Log($"UNKEYED; sent {sent} mic frames (~{sent * 0.02:F1} s), peak {20 * Math.Log10(Math.Max(1, peak) / 32768.0):F1} dBFS");
    }

    var listenEnd = clock.Elapsed + TimeSpan.FromSeconds(listen);
    while (clock.Elapsed < listenEnd && session.State == Iax2SessionState.Connected) await Task.Delay(250);

    Log($"audio-active frames received: {activeFrames} (~{activeFrames * 0.02:F1} s of non-silent audio)");
    Summary(session, voiceFrames, tracePath);
    await session.DisconnectAsync();
    await Task.Delay(500);
    Log($"final state={session.State} reason={session.DisconnectReason}");
    return 0;
}

// Opens every WaveIn input (and the -1 "default" mapper the app uses) at once for 4 s and prints each one's
// level, so the input that actually hears the speaker can be identified. Levels only; nothing is stored.
static async Task<int> MicLevelsAsync()
{
    int count = NAudio.Wave.WaveInEvent.DeviceCount;
    var ids = Enumerable.Range(-1, count + 1).ToArray();           // -1 = default mapper, then 0..n-1
    var sumSq = new double[ids.Length];
    var peaks = new int[ids.Length];
    var counts = new long[ids.Length];
    var ins = new List<NAudio.Wave.WaveInEvent>();

    Console.WriteLine("Get ready - recording every input for 4 s starts in 6 s. Speak normally...");
    await Task.Delay(6000);
    Console.WriteLine("RECORDING - speak now.");
    for (int k = 0; k < ids.Length; k++)
    {
        int idx = k;
        try
        {
            var w = new NAudio.Wave.WaveInEvent { DeviceNumber = ids[k], WaveFormat = new NAudio.Wave.WaveFormat(8000, 16, 1), BufferMilliseconds = 20 };
            w.DataAvailable += (_, e) =>
            {
                for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
                {
                    short s = BitConverter.ToInt16(e.Buffer, i);
                    lock (sumSq) { sumSq[idx] += (double)s * s; counts[idx]++; if (Math.Abs((int)s) > peaks[idx]) peaks[idx] = Math.Abs((int)s); }
                }
            };
            w.StartRecording();
            ins.Add(w);
        }
        catch (Exception ex) { Console.WriteLine($"  input {ids[k]}: could not open ({ex.Message})"); }
    }
    await Task.Delay(4000);
    foreach (var w in ins) { try { w.StopRecording(); w.Dispose(); } catch { } }

    double Db(double v) => v <= 0 ? double.NegativeInfinity : 20 * Math.Log10(v / 32768.0);
    for (int k = 0; k < ids.Length; k++)
    {
        string name = ids[k] < 0 ? "(default mapper = what NodeX uses)" : NAudio.Wave.WaveInEvent.GetCapabilities(ids[k]).ProductName;
        double rms = counts[k] == 0 ? 0 : Math.Sqrt(sumSq[k] / counts[k]);
        Console.WriteLine($"  [{ids[k],2}] {name,-42} RMS {Db(rms),7:F1} dBFS  peak {Db(peaks[k]),7:F1} dBFS  ({counts[k]} samples)");
    }
    return 0;
}

// Speaker-path check without the microphone: a 1 kHz tone through the production playback code, fading in
// from -46 dBFS to -20 dBFS over 5 s so it can never start loud.
static async Task<int> ToneAsync()
{
    await using var dev = new NodeX.UI.Desktop.Audio.NAudioDeviceService();
    dev.ErrorOccurred += e => Console.WriteLine("DEVICE ERROR: " + e);
    dev.StartPlayback();
    Console.WriteLine("Playing a 1 kHz tone on the default output, fading in over 5 s (-46 to -20 dBFS)...");

    const int total = 8000 * 6;
    var tone = new short[total];
    for (int i = 0; i < total; i++)
    {
        double t = Math.Min(1.0, i / (8000.0 * 5));
        double db = -46 + 26 * t;
        tone[i] = (short)(Math.Sin(2 * Math.PI * 1000 * i / 8000.0) * 32767 * Math.Pow(10, db / 20));
    }
    var sw = Stopwatch.StartNew();
    for (int i = 0; i + 160 <= tone.Length; i += 160)
    {
        dev.EnqueuePlaybackPcm(tone.AsMemory(i, 160));
        while (dev.BufferedPlayback > TimeSpan.FromMilliseconds(300)) await Task.Delay(10);
    }
    while (dev.BufferedPlayback > TimeSpan.FromMilliseconds(30)) await Task.Delay(20);
    await Task.Delay(300);
    Console.WriteLine($"done in {sw.Elapsed.TotalSeconds:F1} s. Could you hear it, and from which device?");
    return 0;
}

// Local audio check, no network and no keying: lists devices, records 4 s from the default microphone,
// reports levels, pushes the recording through the real G.711 codec and plays it on the default speaker.
// The recording stays in memory and is never written anywhere.
static async Task<int> AudioAsync()
{
    Console.WriteLine("Input devices:");
    for (int i = 0; i < NAudio.Wave.WaveInEvent.DeviceCount; i++)
        Console.WriteLine($"  [{i}] {NAudio.Wave.WaveInEvent.GetCapabilities(i).ProductName}");
    Console.WriteLine("Output: system default device (as the app uses).");
    try
    {
        using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        foreach (var role in new[] { NAudio.CoreAudioApi.Role.Console, NAudio.CoreAudioApi.Role.Communications })
        {
            var d = en.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Capture, role);
            Console.WriteLine($"Windows default capture ({role}): '{d.FriendlyName}'  volume={d.AudioEndpointVolume.MasterVolumeLevelScalar:P0}  muted={d.AudioEndpointVolume.Mute}");
        }
        var o = en.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Console);
        Console.WriteLine($"Windows default render (Console): '{o.FriendlyName}'  volume={o.AudioEndpointVolume.MasterVolumeLevelScalar:P0}  muted={o.AudioEndpointVolume.Mute}");
    }
    catch (Exception ex) { Console.WriteLine("(could not query default devices: " + ex.Message + ")"); }
    if (NAudio.Wave.WaveInEvent.DeviceCount == 0) { Console.WriteLine("FAIL: no input device visible to this process."); return 1; }

    await using var dev = new NodeX.UI.Desktop.Audio.NAudioDeviceService();
    dev.ErrorOccurred += e => Console.WriteLine("DEVICE ERROR: " + e);

    // ---- record ----
    Console.WriteLine("\nGet ready (headphones on). Recording starts in 6 s...");
    await Task.Delay(6000);
    var pcm = new List<short>();
    var blockSizes = new List<int>();
    var sw = Stopwatch.StartNew();
    Console.WriteLine("\nRECORDING 4 s from the default microphone - say something now...");
    dev.StartCapture(block =>
    {
        lock (pcm) { pcm.AddRange(block.ToArray()); blockSizes.Add(block.Length); }
    });
    await Task.Delay(4000);
    dev.StopCapture();
    short[] rec;
    lock (pcm) rec = pcm.ToArray();
    double secs = rec.Length / 8000.0;
    double rms = rec.Length == 0 ? 0 : Math.Sqrt(rec.Select(s => (double)s * s).Average());
    int peak = rec.Length == 0 ? 0 : rec.Max(s => Math.Abs((int)s));
    double Db(double v) => v <= 0 ? double.NegativeInfinity : 20 * Math.Log10(v / 32768.0);
    Console.WriteLine($"captured {rec.Length} samples = {secs:F2} s of audio in {sw.Elapsed.TotalSeconds:F2} s wall clock ({blockSizes.Count} blocks, typ. {(blockSizes.Count == 0 ? 0 : blockSizes.GroupBy(b => b).OrderByDescending(g => g.Count()).First().Key)} samples)");
    Console.WriteLine($"level: RMS {Db(rms):F1} dBFS, peak {Db(peak):F1} dBFS" +
        (peak == 0 ? "  <-- digital silence: mic muted/blocked or wrong device?" : peak < 300 ? "  (very quiet)" : ""));

    // ---- codec round trip ----
    byte[] ulaw = new byte[rec.Length];
    NodeX.Core.G711Ulaw.Encode(rec, ulaw);
    short[] decoded = new short[ulaw.Length];
    NodeX.Core.G711Ulaw.Decode(ulaw, decoded);
    double err = rec.Length == 0 ? 0 : Math.Sqrt(rec.Zip(decoded, (a, b) => (double)(a - b) * (a - b)).Average());
    Console.WriteLine($"G.711 round trip: RMS error {Db(err):F1} dBFS (signal {Db(rms):F1} dBFS)");

    // ---- the same recording through NodeX's transmit processing (high-pass + auto gain + limiter) ----
    var proc = new NodeX.Core.Audio.MicProcessor();
    short[] processed = (short[])rec.Clone();
    for (int i = 0; i + 160 <= processed.Length; i += 160) proc.Process(processed.AsSpan(i, 160));
    short[] procDecoded = NodeX.Core.G711Ulaw.Decode(NodeX.Core.G711Ulaw.Encode(processed));
    double Rms(short[] a, int from) => a.Length <= from ? 0 : Math.Sqrt(a.Skip(from).Select(s => (double)s * s).Average());
    int Pk(short[] a) => a.Length == 0 ? 0 : a.Max(s => Math.Abs((int)s));
    Console.WriteLine($"processed: RMS {Db(Rms(procDecoded, 0)):F1} dBFS (last 2 s: {Db(Rms(procDecoded, 16000)):F1}), peak {Db(Pk(procDecoded)):F1} dBFS, AGC gain now {proc.CurrentGainDb:+0.0;-0.0} dB   [raw was RMS {Db(rms):F1}, peak {Db(peak):F1}]");

    // ---- play back: RAW, a beep, then PROCESSED ----
    short[] beep = new short[1200];
    for (int i = 0; i < beep.Length; i++) beep[i] = (short)(Math.Sin(2 * Math.PI * 1000 * i / 8000.0) * 32767 * 0.06);
    short[] gap = new short[4000];
    short[] sequence = decoded.Concat(gap).Concat(beep).Concat(gap).Concat(procDecoded).ToArray();
    Console.WriteLine("\nPLAYING: 1) RAW recording, 2) a beep, 3) the PROCESSED version (both through µ-law)...");
    dev.StartPlayback();
    decoded = sequence;
    for (int i = 0; i + 160 <= decoded.Length; i += 160)
    {
        dev.EnqueuePlaybackPcm(decoded.AsMemory(i, 160));
        while (dev.BufferedPlayback > TimeSpan.FromMilliseconds(400)) await Task.Delay(10);
    }
    Console.WriteLine($"queued; buffered {dev.BufferedPlayback.TotalMilliseconds:F0} ms, IsPlaying={dev.IsPlaying}");
    while (dev.BufferedPlayback > TimeSpan.FromMilliseconds(30)) await Task.Delay(20);
    await Task.Delay(300);
    dev.StopPlayback();
    Console.WriteLine("playback finished. Did you hear your recording? (answer in chat)");
    return peak == 0 ? 1 : 0;
}

// Simulates a network handoff: call A is abandoned WITHOUT a HANGUP (its socket is simply closed, like a phone
// that lost its network), then call B is dialled at once from a fresh socket (new source port). Never keys.
static async Task<int> RedialAsync(string target)
{
    string node = Env("NODEX_NODE");
    if (node.Length == 0) { Console.Error.WriteLine("Set NODEX_NODE."); return 2; }
    var ep = (await new NodeResolution().ResolveNodeAsync(target))?.EndPoint;
    if (ep == null) { Console.Error.WriteLine("Could not resolve " + target); return 1; }

    var clock = Stopwatch.StartNew();
    void Log(string s) => Console.WriteLine($"+{clock.Elapsed.TotalSeconds,7:F3}s {s}");

    async Task<Iax2Session> DialAsync(string tag)
    {
        var s = new Iax2Session { Username = "radio", LocalNodeNumber = node, RemoteNodeNumber = target };
        s.PacketTrace += (o, b) => Log($"[{tag}] " + (o ? "TX " : "RX ") + Decode(b.Span));
        s.StateChanged += st => Log($"[{tag}] STATE {st}");
        s.ErrorOccurred += e => Log($"[{tag}] ERROR {e}");
        await s.ConnectAsync(ep);
        var deadline = clock.Elapsed + TimeSpan.FromSeconds(15);
        while (s.State is Iax2SessionState.Connecting or Iax2SessionState.CallTokenHandshake && clock.Elapsed < deadline)
            await Task.Delay(50);
        return s;
    }

    var a = await DialAsync("A");
    Log($"A state={a.State}");
    if (a.State != Iax2SessionState.Connected) return 1;
    await Task.Delay(2000);

    Log("--- abandoning A silently (socket closed, no HANGUP) ---");
    var t = typeof(Iax2Session);
    var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
    ((System.Net.Sockets.Socket)t.GetField("_socket", flags)!.GetValue(a)!).Dispose();
    ((CancellationTokenSource)t.GetField("_cts", flags)!.GetValue(a)!).Cancel();

    var b = await DialAsync("B");
    Log($"B state={b.State} reason={b.DisconnectReason}");
    if (b.State == Iax2SessionState.Connected)
    {
        Log("holding B for 20 s (stale A is still on the remote)");
        var end = clock.Elapsed + TimeSpan.FromSeconds(20);
        while (clock.Elapsed < end && b.State == Iax2SessionState.Connected) await Task.Delay(250);
        Log($"B after hold: state={b.State} reason={b.DisconnectReason}");
        await b.DisconnectAsync();
    }
    return b.State is Iax2SessionState.Connected or Iax2SessionState.Disconnecting or Iax2SessionState.Disconnected
           && b.DisconnectReason is Iax2DisconnectReason.None or Iax2DisconnectReason.LocalRequest ? 0 : 1;
}

static void Summary(Iax2Session s, int voiceFrames, string tracePath)
{
    Console.WriteLine($"SUMMARY state={s.State} reason={s.DisconnectReason} rtt={s.RoundTripTime.TotalMilliseconds:F0}ms voiceFramesRx={voiceFrames}");
    Console.WriteLine($"Trace saved to {tracePath}");
}

// Independent frame decoder (does not use Core's parser, so it also cross-checks the wire format).
static string Decode(ReadOnlySpan<byte> p)
{
    string hex = Convert.ToHexString(p[..Math.Min(p.Length, 48)]) + (p.Length > 48 ? "..." : "");
    if (p.Length < 4) return $"short({p.Length}) {hex}";
    bool full = (p[0] & 0x80) != 0;
    if (!full)
    {
        int src = BinaryPrimitives.ReadUInt16BigEndian(p) & 0x7FFF;
        return $"MINI src={src} ts16={BinaryPrimitives.ReadUInt16BigEndian(p[2..])} payload={p.Length - 4}B";
    }
    if (p.Length < 12) return $"truncated-full({p.Length}) {hex}";
    int s = BinaryPrimitives.ReadUInt16BigEndian(p) & 0x7FFF;
    int d = BinaryPrimitives.ReadUInt16BigEndian(p[2..]) & 0x7FFF;
    bool retrans = (p[2] & 0x80) != 0;
    uint ts = BinaryPrimitives.ReadUInt32BigEndian(p[4..]);
    byte o = p[8], i = p[9], type = p[10], sub = (byte)(p[11] & 0x7F);
    string tname = type switch { 1 => "DTMF", 2 => "VOICE", 4 => "CONTROL", 6 => "IAX", 7 => "TEXT", 3 => "VIDEO", 5 => "NULL", _ => $"type{type}" };
    string sname = type == 6 ? IaxName(sub) : type == 4 ? CtlName(sub) : type == 1 ? $"'{(char)p[11]}'" : $"0x{sub:X2}";
    var sb = new StringBuilder($"FULL{(retrans ? "(R)" : "")} src={s} dst={d} ts={ts} o={o} i={i} {tname}/{sname} len={p.Length}");
    var body = p[12..];
    if (type == 6 && body.Length > 0 && sub is 1 or 7 or 8 or 9 or 5 or 6 or 0x28) sb.Append(" IEs[" + Ies(body) + "]");
    else if (type == 7) sb.Append(" text='" + Encoding.UTF8.GetString(body).TrimEnd('\0') + "'");
    else if (body.Length > 0) sb.Append(" " + Convert.ToHexString(body[..Math.Min(body.Length, 32)]));
    return sb.ToString();
}

static string Ies(ReadOnlySpan<byte> b)
{
    var parts = new List<string>();
    int pos = 0;
    while (pos + 2 <= b.Length)
    {
        int id = b[pos], len = b[pos + 1];
        if (pos + 2 + len > b.Length) { parts.Add("<bad IE length>"); break; }
        var data = b.Slice(pos + 2, len);
        bool text = len > 0 && data.ToArray().All(c => c >= 0x20 && c < 0x7F);
        string v = len == 0 ? "(empty)" : text ? $"'{Encoding.ASCII.GetString(data)}'" : Convert.ToHexString(data);
        parts.Add($"{id}:{v}");
        pos += 2 + len;
    }
    return string.Join(" ", parts);
}

static string IaxName(int s) => s switch
{
    1 => "NEW", 2 => "PING", 3 => "PONG", 4 => "ACK", 5 => "HANGUP", 6 => "REJECT", 7 => "ACCEPT",
    8 => "AUTHREQ", 9 => "AUTHREP", 0x0A => "INVAL", 0x0B => "LAGRQ", 0x0C => "LAGRP", 0x12 => "VNAK",
    0x1E => "POKE", 0x28 => "CALLTOKEN", _ => $"iax0x{s:X2}"
};

static string CtlName(int s) => s switch
{
    1 => "HANGUP", 3 => "RINGING", 4 => "ANSWER", 5 => "BUSY", 8 => "CONGESTION", 0x0B => "OPTION",
    0x0C => "KEY", 0x0D => "UNKEY", 0x0E => "PROGRESS", 0x0F => "PROCEEDING", _ => $"ctl0x{s:X2}"
};
