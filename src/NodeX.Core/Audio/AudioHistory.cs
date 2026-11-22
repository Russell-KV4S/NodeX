using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using NodeX.Core.Diagnostics;

namespace NodeX.Core.Audio;

public enum AudioClipDirection
{
    Rx,
    Tx
}

/// <summary>Metadata for one saved received, transmitted, or combined voice clip.</summary>
public sealed record AudioClip(
    string Id,
    DateTimeOffset TimestampUtc,
    string Node,
    string NodeLabel,
    AudioClipDirection Direction,
    double DurationSeconds,
    string FileName,
    long SizeBytes,
    bool IsUnheard = true,
    bool IsCombined = false);

/// <summary>
/// Persists recent RX/TX audio clips locally as standard 8 kHz 16-bit mono WAV files alongside a lightweight
/// <c>index.json</c>. Automatically prunes the oldest clips above <see cref="MaxClips"/> so disk use stays bounded.
/// Never uploaded anywhere.
/// </summary>
public sealed class AudioHistoryStore
{
    public const int DefaultMaxClips = 50;
    public const int MinMaxClips = 10;
    public const int MaxAllowedClips = 200;
    public const int CombineGapMilliseconds = 400;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _directory;
    private readonly string _indexPath;
    private readonly Lock _lock = new();
    private List<AudioClip> _clips = [];
    private int _maxClips = DefaultMaxClips;

    public AudioHistoryStore(string directory, int maxClips = DefaultMaxClips)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _indexPath = Path.Combine(_directory, "index.json");
        _maxClips = Math.Clamp(maxClips, MinMaxClips, MaxAllowedClips);
    }

    public static string FormatBytes(long bytes)
    {
        const long bytesPerGigabyte = 1024L * 1024L * 1024L;
        if (bytes <= 0) return "0 B";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < bytesPerGigabyte) return $"{bytes / (1024.0 * 1024.0):0.0} MB";
        return $"{bytes / (double)bytesPerGigabyte:0.0#} GB";
    }

    public string DirectoryPath => _directory;

    public int MaxClips
    {
        get { lock (_lock) return _maxClips; }
        set
        {
            int clamped = Math.Clamp(value, MinMaxClips, MaxAllowedClips);
            bool changed;
            lock (_lock)
            {
                _maxClips = clamped;
                changed = PruneLocked();
                if (changed) SaveIndexLocked();
            }
            if (changed) Changed?.Invoke();
        }
    }

    public long TotalSizeBytes
    {
        get
        {
            lock (_lock) return TotalSizeBytesLocked();
        }
    }

    /// <summary>Raised on any thread whenever clips are added, combined, marked heard, deleted, or cleared.</summary>
    public event Action? Changed;

    /// <summary>Newest-first snapshot of saved clips.</summary>
    public IReadOnlyList<AudioClip> Clips
    {
        get { lock (_lock) return _clips.ToArray(); }
    }

    public int UnheardCount
    {
        get
        {
            lock (_lock)
            {
                int n = 0;
                foreach (var c in _clips) if (c.IsUnheard && c.Direction == AudioClipDirection.Rx && !c.IsCombined) n++;
                return n;
            }
        }
    }

    public AudioClip? LatestRxClip
    {
        get
        {
            lock (_lock)
            {
                foreach (var c in _clips)
                    if (c.Direction == AudioClipDirection.Rx && !c.IsCombined) return c;
                return _clips.FirstOrDefault(c => c.Direction == AudioClipDirection.Rx);
            }
        }
    }

    public string GetFilePath(AudioClip clip) => Path.Combine(_directory, clip.FileName);

    /// <summary>Loads existing metadata from disk and drops entries whose WAV file no longer exists.</summary>
    public void Load()
    {
        lock (_lock)
        {
            _clips = [];
            if (!File.Exists(_indexPath)) return;
            try
            {
                string json = File.ReadAllText(_indexPath);
                var loaded = JsonSerializer.Deserialize<List<AudioClip>>(json, JsonOptions);
                if (loaded == null) return;
                bool pruned = false;
                foreach (var clip in loaded)
                {
                    if (string.IsNullOrWhiteSpace(clip.FileName)) { pruned = true; continue; }
                    string fileName = Path.GetFileName(clip.FileName);
                    string full = Path.Combine(_directory, fileName);
                    if (!File.Exists(full)) { pruned = true; continue; }
                    long size = clip.SizeBytes > 0 ? clip.SizeBytes : new FileInfo(full).Length;
                    _clips.Add(clip with { FileName = fileName, SizeBytes = size });
                }
                _clips.Sort((a, b) => b.TimestampUtc.CompareTo(a.TimestampUtc));
                if (PruneLocked() || pruned) SaveIndexLocked();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Default.Info("history", $"could not load audio history index: {ex.Message}");
                _clips = [];
            }
        }
        Changed?.Invoke();
    }

    /// <summary>Writes a new WAV clip to disk, inserts it at the top of the list, and prunes oldest beyond configured limits.</summary>
    public AudioClip SaveClip(
        string node,
        string nodeLabel,
        AudioClipDirection direction,
        ReadOnlySpan<short> pcmSamples,
        DateTimeOffset? timestampUtc = null)
    {
        var ts = timestampUtc ?? DateTimeOffset.UtcNow;
        string id = Guid.NewGuid().ToString("N")[..12];
        string safeNode = SanitizeFilePart(node);
        string dirTag = direction == AudioClipDirection.Rx ? "rx" : "tx";
        string fileName = $"{ts:yyyyMMdd-HHmmss}-{safeNode}-{dirTag}-{id}.wav";
        double duration = (double)pcmSamples.Length / AudioFormat.SampleRate;
        byte[] wav = WavCodec.EncodePcm16(pcmSamples, AudioFormat.SampleRate);

        Directory.CreateDirectory(_directory);
        string fullPath = Path.Combine(_directory, fileName);
        File.WriteAllBytes(fullPath, wav);

        var clip = new AudioClip(
            Id: id,
            TimestampUtc: ts,
            Node: node,
            NodeLabel: nodeLabel ?? "",
            Direction: direction,
            DurationSeconds: Math.Round(duration, 2),
            FileName: fileName,
            SizeBytes: wav.LongLength,
            IsUnheard: direction == AudioClipDirection.Rx);

        lock (_lock)
        {
            _clips.Insert(0, clip);
            PruneLocked();
            SaveIndexLocked();
        }
        Changed?.Invoke();
        return clip;
    }

    /// <summary>
    /// Combines two or more saved clips chronologically (oldest to newest) with a short silence gap between them
    /// into one standalone 8 kHz 16-bit mono PCM WAV file and adds it to the library without deleting originals.
    /// </summary>
    public AudioClip CombineClips(IEnumerable<string> clipIds, string? customTitle = null, DateTimeOffset? timestampUtc = null)
    {
        ArgumentNullException.ThrowIfNull(clipIds);
        var wanted = clipIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
        if (wanted.Count < 2)
            throw new InvalidOperationException("Select at least 2 audio clips to combine.");

        List<AudioClip> selected;
        lock (_lock)
        {
            selected = _clips
                .Where(c => wanted.Contains(c.Id) && File.Exists(Path.Combine(_directory, c.FileName)))
                .OrderBy(c => c.TimestampUtc)
                .ThenBy(c => c.Id, StringComparer.Ordinal)
                .ToList();
        }

        if (selected.Count < 2)
            throw new InvalidOperationException("At least 2 saved audio files must still exist on disk to combine.");

        var ts = timestampUtc ?? DateTimeOffset.UtcNow;
        string id = Guid.NewGuid().ToString("N")[..12];
        string fileName = $"{ts:yyyyMMdd-HHmmss}-combined-{selected.Count}clips-{id}.wav";
        Directory.CreateDirectory(_directory);
        string fullPath = Path.Combine(_directory, fileName);
        string tempPath = fullPath + ".tmp";

        long totalPcmBytes = 0;
        const long MaxPcmBytes = int.MaxValue - WavCodec.HeaderBytes;
        int gapBytes = (AudioFormat.SampleRate * CombineGapMilliseconds / 1000) * sizeof(short);
        byte[] gapSilence = new byte[gapBytes];
        byte[] copyBuf = new byte[16 * 1024];

        try
        {
            using (var outStream = File.Create(tempPath))
            {
                Span<byte> header = stackalloc byte[WavCodec.HeaderBytes];
                header.Clear();
                outStream.Write(header);

                for (int idx = 0; idx < selected.Count; idx++)
                {
                    if (idx > 0)
                    {
                        if (totalPcmBytes + gapBytes > MaxPcmBytes)
                            throw new InvalidOperationException("Combined recording would exceed the 2 GB WAV file limit.");
                        outStream.Write(gapSilence, 0, gapSilence.Length);
                        totalPcmBytes += gapBytes;
                    }

                    string srcPath = Path.Combine(_directory, selected[idx].FileName);
                    using var inStream = File.OpenRead(srcPath);
                    if (!WavCodec.TryReadPcm16DataChunk(inStream, out int sampleRate, out long dataOffset, out int dataBytes))
                        throw new InvalidDataException($"Clip \"{selected[idx].FileName}\" is not a valid 16-bit mono WAV file.");

                    inStream.Position = dataOffset;
                    if (sampleRate == AudioFormat.SampleRate)
                    {
                        if (totalPcmBytes + dataBytes > MaxPcmBytes)
                            throw new InvalidOperationException("Combined recording would exceed the 2 GB WAV file limit.");

                        int remaining = dataBytes;
                        while (remaining > 0)
                        {
                            int n = inStream.Read(copyBuf, 0, Math.Min(copyBuf.Length, remaining));
                            if (n == 0) break;
                            int even = n & ~1;
                            if (even > 0)
                            {
                                outStream.Write(copyBuf, 0, even);
                                totalPcmBytes += even;
                            }
                            remaining -= n;
                        }
                    }
                    else
                    {
                        var resampler = new PcmResampler(sampleRate, AudioFormat.SampleRate);
                        int sampleChunkSize = copyBuf.Length / 2;
                        short[] inSamples = new short[sampleChunkSize];
                        short[] outSamples = new short[resampler.MaxOutputSamples(sampleChunkSize)];
                        byte[] outBytes = new byte[outSamples.Length * 2];

                        int remaining = dataBytes;
                        while (remaining >= 2)
                        {
                            int toRead = Math.Min(copyBuf.Length, remaining & ~1);
                            int n = inStream.Read(copyBuf, 0, toRead);
                            int count = n / 2;
                            if (count == 0) break;

                            for (int i = 0; i < count; i++)
                                inSamples[i] = BinaryPrimitives.ReadInt16LittleEndian(copyBuf.AsSpan(i * 2, 2));

                            int outCount = resampler.Process(inSamples.AsSpan(0, count), outSamples);
                            int writeBytes = outCount * 2;
                            if (totalPcmBytes + writeBytes > MaxPcmBytes)
                                throw new InvalidOperationException("Combined recording would exceed the 2 GB WAV file limit.");

                            for (int i = 0; i < outCount; i++)
                                BinaryPrimitives.WriteInt16LittleEndian(outBytes.AsSpan(i * 2, 2), outSamples[i]);
                            if (writeBytes > 0)
                            {
                                outStream.Write(outBytes, 0, writeBytes);
                                totalPcmBytes += writeBytes;
                            }
                            remaining -= n;
                        }
                    }
                }

                outStream.Position = 0;
                WavCodec.WriteHeader(header, (int)totalPcmBytes, AudioFormat.SampleRate);
                outStream.Write(header);
            }

            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }

        var distinctNodes = selected
            .Select(c => c.Node)
            .Where(n => !string.Equals(n, "Combined", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctNodes.Count == 0)
            distinctNodes = selected.Select(c => c.Node).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        string node = distinctNodes.Count == 1 ? distinctNodes[0] : "Combined";
        string label;
        string trimmedCustom = (customTitle ?? "").Trim();
        if (trimmedCustom.Length > 0)
        {
            label = trimmedCustom.Length > 60 ? trimmedCustom[..60].TrimEnd() : trimmedCustom;
        }
        else if (distinctNodes.Count == 1)
        {
            label = $"Combined ({selected.Count} clips)";
        }
        else
        {
            label = $"Combined · {distinctNodes.Count} nodes ({selected.Count} clips)";
        }

        double duration = (double)totalPcmBytes / (AudioFormat.SampleRate * sizeof(short));
        var combined = new AudioClip(
            Id: id,
            TimestampUtc: ts,
            Node: node,
            NodeLabel: label,
            Direction: AudioClipDirection.Rx,
            DurationSeconds: Math.Round(duration, 2),
            FileName: fileName,
            SizeBytes: WavCodec.HeaderBytes + totalPcmBytes,
            IsUnheard: false,
            IsCombined: true);

        lock (_lock)
        {
            _clips.Insert(0, combined);
            PruneLocked();
            SaveIndexLocked();
        }
        Changed?.Invoke();
        return combined;
    }

    public void MarkHeard(string clipId)
    {
        bool changed = false;
        lock (_lock)
        {
            for (int i = 0; i < _clips.Count; i++)
            {
                if (_clips[i].Id == clipId && _clips[i].IsUnheard)
                {
                    _clips[i] = _clips[i] with { IsUnheard = false };
                    changed = true;
                    break;
                }
            }
            if (changed) SaveIndexLocked();
        }
        if (changed) Changed?.Invoke();
    }

    public void MarkAllHeard()
    {
        bool changed = false;
        lock (_lock)
        {
            for (int i = 0; i < _clips.Count; i++)
            {
                if (_clips[i].IsUnheard)
                {
                    _clips[i] = _clips[i] with { IsUnheard = false };
                    changed = true;
                }
            }
            if (changed) SaveIndexLocked();
        }
        if (changed) Changed?.Invoke();
    }

    public bool Delete(string clipId)
    {
        AudioClip? removed = null;
        lock (_lock)
        {
            int idx = _clips.FindIndex(c => c.Id == clipId);
            if (idx >= 0)
            {
                removed = _clips[idx];
                _clips.RemoveAt(idx);
                SaveIndexLocked();
            }
        }
        if (removed == null) return false;
        TryDeleteFile(Path.Combine(_directory, removed.FileName));
        Changed?.Invoke();
        return true;
    }

    public int DeleteMany(IEnumerable<string> clipIds)
    {
        ArgumentNullException.ThrowIfNull(clipIds);
        var ids = clipIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0) return 0;

        List<AudioClip> removed = [];
        lock (_lock)
        {
            for (int i = _clips.Count - 1; i >= 0; i--)
            {
                if (ids.Contains(_clips[i].Id))
                {
                    removed.Add(_clips[i]);
                    _clips.RemoveAt(i);
                }
            }
            if (removed.Count > 0) SaveIndexLocked();
        }
        if (removed.Count == 0) return 0;
        foreach (var clip in removed) TryDeleteFile(Path.Combine(_directory, clip.FileName));
        Changed?.Invoke();
        return removed.Count;
    }

    public void ClearAll()
    {
        List<AudioClip> old;
        lock (_lock)
        {
            if (_clips.Count == 0) return;
            old = _clips;
            _clips = [];
            SaveIndexLocked();
        }
        foreach (var clip in old) TryDeleteFile(Path.Combine(_directory, clip.FileName));
        Changed?.Invoke();
    }

    private long TotalSizeBytesLocked()
    {
        long total = 0;
        foreach (var c in _clips) total += Math.Max(0, c.SizeBytes);
        return total;
    }

    private bool PruneLocked()
    {
        bool pruned = false;
        while (_clips.Count > _maxClips)
        {
            var oldest = _clips[^1];
            _clips.RemoveAt(_clips.Count - 1);
            TryDeleteFile(Path.Combine(_directory, oldest.FileName));
            pruned = true;
        }
        return pruned;
    }

    private void SaveIndexLocked()
    {
        try
        {
            Directory.CreateDirectory(_directory);
            string json = JsonSerializer.Serialize(_clips, JsonOptions);
            string tmp = _indexPath + ".tmp";
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, _indexPath, overwrite: true);
                    break;
                }
                catch (Exception ex) when (attempt < 5 && (ex is IOException or UnauthorizedAccessException))
                {
                    // Antivirus or file indexers on Windows can transiently lock index.json right after a save.
                    Thread.Sleep(15 * (attempt + 1));
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Default.Info("history", $"could not save audio history index: {ex.Message}");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }

    private static string SanitizeFilePart(string s)
    {
        Span<char> buf = stackalloc char[Math.Min(24, s.Length)];
        int n = 0;
        foreach (char c in s)
        {
            if (n >= buf.Length) break;
            buf[n++] = char.IsLetterOrDigit(c) ? c : '_';
        }
        return n > 0 ? new string(buf[..n]) : "node";
    }
}

/// <summary>
/// Captures incoming RX overs and outgoing TX overs from live <see cref="NodeLink"/>s and writes finished clips to
/// <see cref="AudioHistoryStore"/> off the audio thread. Zero allocations while idle/silent.
/// </summary>
public sealed class AudioClipRecorder : IDisposable
{
    /// <summary>Ignore blips/kerchunks shorter than this so the history list stays clean.</summary>
    public const int MinClipSamples = AudioFormat.SampleRate * 3 / 10;        // 300 ms

    /// <summary>Safety cap on a single clip (5 minutes ≈ 4.8 MB).</summary>
    public const int MaxClipSamples = AudioFormat.SampleRate * 300;         // 5 min

    /// <summary>How long silence lasts after audio stops before finalizing an RX over.</summary>
    public const int RxSilenceHangFrames = 60;                              // 1.2 s (60 × 20 ms)

    private readonly AudioHistoryStore _store;
    private readonly Func<bool> _isEnabled;
    private readonly Func<string, string> _labelResolver;
    private readonly Lock _lock = new();
    private readonly Dictionary<NodeLink, LinkCapture> _captures = [];
    private bool _disposed;

    public AudioClipRecorder(
        AudioHistoryStore store,
        Func<bool> isEnabled,
        Func<string, string>? labelResolver = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
        _labelResolver = labelResolver ?? (_ => "");
    }

    public void Attach(NodeLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        lock (_lock)
        {
            if (_disposed || _captures.ContainsKey(link)) return;
            var cap = new LinkCapture(this, link);
            _captures[link] = cap;
        }
    }

    public void Detach(NodeLink link)
    {
        LinkCapture? cap;
        lock (_lock)
        {
            if (!_captures.Remove(link, out cap)) return;
        }
        cap.FlushAndDetach();
    }

    internal void FlushBuffer(string node, AudioClipDirection direction, short[] samples, DateTimeOffset startedUtc)
    {
        if (samples.Length < MinClipSamples) return;
        string label = _labelResolver(node);
        _ = Task.Run(() =>
        {
            try { _store.SaveClip(node, label, direction, samples, startedUtc); }
            catch (Exception ex) { DiagnosticLog.Default.Info("history", $"failed to save {direction} clip for {node}: {ex.Message}"); }
        });
    }

    public void Dispose()
    {
        LinkCapture[] all;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            all = [.. _captures.Values];
            _captures.Clear();
        }
        foreach (var cap in all) cap.FlushAndDetach();
    }

    private sealed class LinkCapture
    {
        private readonly AudioClipRecorder _owner;
        private readonly NodeLink _link;
        private readonly string _node;
        private readonly Lock _rxLock = new();
        private readonly Lock _txLock = new();

        private List<short>? _rxBuffer;
        private DateTimeOffset _rxStarted;
        private int _rxSilentFrames;
        private volatile bool _remoteKeyed;

        private List<short>? _txBuffer;
        private DateTimeOffset _txStarted;

        public LinkCapture(AudioClipRecorder owner, NodeLink link)
        {
            _owner = owner;
            _link = link;
            _node = link.Target.NodeNumber;
            _link.Audio.RxFrameDecoded += OnRxFrame;
            _link.Audio.TxFrameCaptured += OnTxFrame;
            _link.Session.RemoteKeyedChanged += OnRemoteKeyedChanged;
            _link.Session.TransmittingChanged += OnTransmittingChanged;
        }

        private void OnRxFrame(ReadOnlyMemory<short> frame)
        {
            var span = frame.Span;
            if (!_owner._isEnabled())
            {
                FlushRx();
                return;
            }

            bool hasSound = PcmHasSound(span);
            short[]? ready = null;
            DateTimeOffset started = default;

            lock (_rxLock)
            {
                if (_rxBuffer == null)
                {
                    if (!hasSound) return;
                    _rxBuffer = new List<short>(AudioFormat.SampleRate * 4);
                    _rxStarted = DateTimeOffset.UtcNow;
                    _rxSilentFrames = 0;
                }

                if (hasSound) _rxSilentFrames = 0;
                else _rxSilentFrames++;

                Append(_rxBuffer, span);

                if (_rxBuffer.Count >= MaxClipSamples || (!_remoteKeyed && _rxSilentFrames >= RxSilenceHangFrames))
                {
                    int trimTail = Math.Min(_rxBuffer.Count, Math.Max(0, _rxSilentFrames - 10) * AudioFormat.SamplesPerFrame);
                    int take = _rxBuffer.Count - trimTail;
                    if (take >= MinClipSamples)
                    {
                        ready = new short[take];
                        _rxBuffer.CopyTo(0, ready, 0, take);
                        started = _rxStarted;
                    }
                    _rxBuffer = null;
                    _rxSilentFrames = 0;
                }
            }

            if (ready != null) _owner.FlushBuffer(_node, AudioClipDirection.Rx, ready, started);
        }

        private void OnRemoteKeyedChanged(bool keyed)
        {
            _remoteKeyed = keyed;
            if (!keyed) FlushRx();
        }

        private static bool PcmHasSound(ReadOnlySpan<short> pcm)
        {
            if (pcm.IsEmpty) return false;
            double sumSq = 0;
            for (int i = 0; i < pcm.Length; i++)
            {
                double s = pcm[i] / 32768.0;
                sumSq += s * s;
            }
            double rms = Math.Sqrt(sumSq / pcm.Length);
            if (rms <= 0) return false;
            double dbfs = 20.0 * Math.Log10(rms);
            return dbfs > RxActivity.SoundRmsDbfs;
        }

        private void OnTxFrame(ReadOnlyMemory<short> frame)
        {
            if (!_owner._isEnabled())
            {
                FlushTx();
                return;
            }

            short[]? ready = null;
            DateTimeOffset started = default;
            lock (_txLock)
            {
                if (_txBuffer == null)
                {
                    _txBuffer = new List<short>(AudioFormat.SampleRate * 4);
                    _txStarted = DateTimeOffset.UtcNow;
                }
                Append(_txBuffer, frame.Span);
                if (_txBuffer.Count >= MaxClipSamples)
                {
                    ready = [.. _txBuffer];
                    started = _txStarted;
                    _txBuffer = null;
                }
            }
            if (ready != null) _owner.FlushBuffer(_node, AudioClipDirection.Tx, ready, started);
        }

        private void OnTransmittingChanged(bool transmitting)
        {
            if (!transmitting) FlushTx();
        }

        private void FlushRx()
        {
            short[]? ready = null;
            DateTimeOffset started = default;
            lock (_rxLock)
            {
                if (_rxBuffer != null && _rxBuffer.Count >= MinClipSamples)
                {
                    int trimTail = Math.Min(_rxBuffer.Count, Math.Max(0, _rxSilentFrames - 10) * AudioFormat.SamplesPerFrame);
                    int take = _rxBuffer.Count - trimTail;
                    if (take >= MinClipSamples)
                    {
                        ready = new short[take];
                        _rxBuffer.CopyTo(0, ready, 0, take);
                        started = _rxStarted;
                    }
                }
                _rxBuffer = null;
                _rxSilentFrames = 0;
            }
            if (ready != null) _owner.FlushBuffer(_node, AudioClipDirection.Rx, ready, started);
        }

        private void FlushTx()
        {
            short[]? ready = null;
            DateTimeOffset started = default;
            lock (_txLock)
            {
                if (_txBuffer != null && _txBuffer.Count >= MinClipSamples)
                {
                    ready = [.. _txBuffer];
                    started = _txStarted;
                }
                _txBuffer = null;
            }
            if (ready != null) _owner.FlushBuffer(_node, AudioClipDirection.Tx, ready, started);
        }

        public void FlushAndDetach()
        {
            _link.Audio.RxFrameDecoded -= OnRxFrame;
            _link.Audio.TxFrameCaptured -= OnTxFrame;
            _link.Session.RemoteKeyedChanged -= OnRemoteKeyedChanged;
            _link.Session.TransmittingChanged -= OnTransmittingChanged;
            FlushRx();
            FlushTx();
        }

        private static void Append(List<short> list, ReadOnlySpan<short> span)
        {
            for (int i = 0; i < span.Length; i++) list.Add(span[i]);
        }
    }
}

/// <summary>
/// Replays a saved <see cref="AudioClip"/> locally through the platform's <see cref="IAudioDeviceService"/>.
/// Strictly local playback only — never touches IAX2 or PTT.
/// </summary>
public sealed class AudioClipPlayer : IAsyncDisposable
{
    private readonly Func<IAudioDeviceService> _audioFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _playCts;
    private Task? _playTask;

    public AudioClipPlayer(Func<IAudioDeviceService> audioFactory)
    {
        _audioFactory = audioFactory ?? throw new ArgumentNullException(nameof(audioFactory));
    }

    public bool IsPlaying { get; private set; }
    public string? CurrentClipId { get; private set; }
    public double PositionSeconds { get; private set; }
    public double DurationSeconds { get; private set; }
    public double Progress => DurationSeconds > 0 ? Math.Clamp(PositionSeconds / DurationSeconds, 0, 1) : 0;

    public event Action? StateChanged;
    public event Action<string>? ErrorOccurred;

    public async Task PlayAsync(AudioClip clip, string filePath)
    {
        ArgumentNullException.ThrowIfNull(clip);
        await StopAsync().ConfigureAwait(false);

        if (!File.Exists(filePath))
        {
            ErrorOccurred?.Invoke("Audio file is no longer available.");
            return;
        }

        short[] samples;
        int sampleRate;
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
            samples = WavCodec.DecodePcm16(bytes, out sampleRate);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"Could not open clip: {ex.Message}");
            return;
        }

        if (samples.Length == 0) return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var cts = new CancellationTokenSource();
            _playCts = cts;
            CurrentClipId = clip.Id;
            DurationSeconds = (double)samples.Length / sampleRate;
            PositionSeconds = 0;
            IsPlaying = true;
            StateChanged?.Invoke();

            _playTask = Task.Run(() => RunPlaybackAsync(samples, sampleRate, cts.Token));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? task;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            cts = _playCts;
            task = _playTask;
            _playCts = null;
            _playTask = null;
        }
        finally
        {
            _gate.Release();
        }

        if (cts != null)
        {
            cts.Cancel();
            if (task != null)
            {
                try { await task.ConfigureAwait(false); } catch (Exception) { }
            }
            cts.Dispose();
        }

        if (IsPlaying || CurrentClipId != null)
        {
            IsPlaying = false;
            CurrentClipId = null;
            PositionSeconds = 0;
            DurationSeconds = 0;
            StateChanged?.Invoke();
        }
    }

    private async Task RunPlaybackAsync(short[] samples, int sampleRate, CancellationToken ct)
    {
        IAudioDeviceService? device = null;
        try
        {
            device = _audioFactory();
            device.StartPlayback();

            int samplesPerFrame = Math.Max(1, sampleRate * AudioFormat.FrameMilliseconds / 1000);
            int offset = 0;
            var sw = Stopwatch.StartNew();
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(AudioFormat.FrameMilliseconds));

            while (!ct.IsCancellationRequested && offset < samples.Length)
            {
                int count = Math.Min(samplesPerFrame, samples.Length - offset);
                device.EnqueuePlaybackPcm(samples.AsMemory(offset, count));
                offset += count;
                PositionSeconds = Math.Min(DurationSeconds, (double)offset / sampleRate);
                StateChanged?.Invoke();

                await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
            }

            // Let the last buffered frame finish playing cleanly on the output device.
            if (!ct.IsCancellationRequested)
            {
                int tailMs = (int)Math.Clamp(device.BufferedPlayback.TotalMilliseconds + 80, 80, 400);
                await Task.Delay(tailMs, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"Playback error: {ex.Message}");
        }
        finally
        {
            if (device != null)
            {
                try { device.StopPlayback(); } catch (Exception) { }
                try { await device.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
            }

            if (!ct.IsCancellationRequested)
            {
                IsPlaying = false;
                CurrentClipId = null;
                PositionSeconds = 0;
                DurationSeconds = 0;
                StateChanged?.Invoke();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
