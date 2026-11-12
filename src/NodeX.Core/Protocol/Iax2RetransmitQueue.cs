namespace NodeX.Core;

/// <summary>
/// Holds reliable full frames until the peer acknowledges them (RFC 5456 section 7 / 7.2.1):
/// retransmits with exponential back-off (capped at 10 s) and reports when the retry limit is exceeded.
/// Time is passed in by the caller so the queue is deterministic and socket-free. Thread-safe.
/// </summary>
internal sealed class Iax2RetransmitQueue
{
    /// <summary>Upper bound on the retransmission timer (RFC 5456 7.2.1).</summary>
    public const long MaxTimeoutMs = 10_000;

    private sealed class Entry(byte seq, byte[] packet, long dueMs, long timeoutMs)
    {
        public readonly byte Seq = seq;
        public readonly byte[] Packet = packet;
        public long DueMs = dueMs;
        public long TimeoutMs = timeoutMs;
        public int Retries;
    }

    private readonly List<Entry> _entries = [];
    private readonly Lock _lock = new();

    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    /// <summary>Tracks a frame that was just sent for the first time.</summary>
    public void Add(byte seq, byte[] packet, long nowMs, long timeoutMs)
    {
        lock (_lock) _entries.Add(new Entry(seq, packet, nowMs + timeoutMs, timeoutMs));
    }

    /// <summary>Releases every frame the peer has received, given its next-expected sequence number.</summary>
    /// <returns>Number of frames released.</returns>
    public int AcknowledgeUpTo(byte peerNextExpected)
    {
        lock (_lock)
            return _entries.RemoveAll(e => Iax2Sequencing.IsAcknowledgedBy(e.Seq, peerNextExpected));
    }

    /// <summary>
    /// Returns copies (with the R bit set) of frames whose timer has expired, doubling their timers.
    /// <paramref name="exceeded"/> becomes true when a frame is due again after <paramref name="maxRetries"/>
    /// retransmissions, meaning the call leg is unusable and must be torn down.
    /// </summary>
    public List<byte[]> CollectDue(long nowMs, int maxRetries, out bool exceeded)
    {
        exceeded = false;
        var due = new List<byte[]>();
        lock (_lock)
        {
            foreach (var e in _entries)
            {
                if (nowMs < e.DueMs) continue;

                if (e.Retries >= maxRetries)
                {
                    exceeded = true;
                    continue;
                }

                e.Retries++;
                e.TimeoutMs = Math.Min(e.TimeoutMs * 2, MaxTimeoutMs);
                e.DueMs = nowMs + e.TimeoutMs;
                due.Add(WithRetransmitBit(e.Packet));
            }
        }
        return due;
    }

    /// <summary>
    /// For a received VNAK: copies of every pending frame at or after the peer's next-expected sequence number.
    /// </summary>
    public List<byte[]> RetransmitFrom(byte peerNextExpected, long nowMs)
    {
        var result = new List<byte[]>();
        lock (_lock)
        {
            foreach (var e in _entries)
            {
                if (unchecked((byte)(e.Seq - peerNextExpected)) >= 128) continue;
                e.DueMs = nowMs + e.TimeoutMs;
                result.Add(WithRetransmitBit(e.Packet));
            }
        }
        return result;
    }

    public void Clear()
    {
        lock (_lock) _entries.Clear();
    }

    /// <summary>True while the frame with this sequence number has not been acknowledged.</summary>
    public bool IsPending(byte seq)
    {
        lock (_lock) return _entries.Exists(e => e.Seq == seq);
    }

    /// <summary>
    /// A copy of one unacknowledged frame with the retransmit bit set, for a caller that paces its own
    /// re-sends (the HANGUP at the end of a call). Null once the frame has been acknowledged. Does not count
    /// against the retry limit.
    /// </summary>
    public byte[]? RetransmitCopy(byte seq)
    {
        lock (_lock)
        {
            var entry = _entries.Find(e => e.Seq == seq);
            return entry == null ? null : WithRetransmitBit(entry.Packet);
        }
    }

    private static byte[] WithRetransmitBit(byte[] original)
    {
        byte[] copy = (byte[])original.Clone();
        copy[2] |= 0x80; // R bit lives in the top bit of the destination call number
        return copy;
    }
}
