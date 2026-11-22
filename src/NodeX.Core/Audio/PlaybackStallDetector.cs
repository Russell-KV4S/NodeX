namespace NodeX.Core.Audio;

/// <summary>Outcome of checking playback progress or an audio write against <see cref="PlaybackStallDetector"/>.</summary>
public readonly record struct PlaybackStallDecision(bool ShouldRebuild, string? Reason)
{
    public static PlaybackStallDecision None => new(false, null);
}

/// <summary>
/// Tracks output progress on a platform audio track (samples written vs. device playback head position)
/// so a stalled, deadlocked, or deep-buffer-blocked track (such as an Android <c>AudioTrack</c> whose
/// <c>PlaybackHeadPosition</c> stops advancing above <see cref="NodeAudioEngineOptions.MaxDeviceBacklog"/>)
/// is detected and rebuilt instead of dropping every future frame silently. Not thread-safe; callers synchronize.
/// </summary>
public sealed class PlaybackStallDetector
{
    private readonly long _stallThresholdMs;
    private readonly long _zeroWriteStallMs;
    private readonly int _minPendingSamples;
    private readonly long _rebuildCooldownMs;

    private bool _hasHeadBaseline;
    private int _lastHeadPosition;
    private bool _hasStallStart;
    private long _stallStartTicks;

    private bool _hasZeroWriteStart;
    private long _zeroWriteStartTicks;

    private bool _hasRebuilt;
    private long _lastRebuildTicks;

    public PlaybackStallDetector(
        long stallThresholdMs = 600,
        long zeroWriteStallMs = 300,
        int minPendingSamples = AudioFormat.SamplesPerFrame * 4,
        long rebuildCooldownMs = 2000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stallThresholdMs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(zeroWriteStallMs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minPendingSamples);
        ArgumentOutOfRangeException.ThrowIfNegative(rebuildCooldownMs);

        _stallThresholdMs = stallThresholdMs;
        _zeroWriteStallMs = zeroWriteStallMs;
        _minPendingSamples = minPendingSamples;
        _rebuildCooldownMs = rebuildCooldownMs;
    }

    /// <summary>Resets all tracking state after a fresh track is built or playback stops.</summary>
    public void Reset()
    {
        _hasHeadBaseline = false;
        _lastHeadPosition = 0;
        _hasStallStart = false;
        _stallStartTicks = 0;
        _hasZeroWriteStart = false;
        _zeroWriteStartTicks = 0;
    }

    /// <summary>
    /// Observes the current <paramref name="samplesWritten"/> and <paramref name="headPosition"/>
    /// (e.g. from <see cref="IAudioDeviceService.BufferedPlayback"/>) and reports if playback appears stalled.
    /// </summary>
    public PlaybackStallDecision ObserveProgress(long samplesWritten, int headPosition, long nowTicks)
    {
        int pending = AudioFormat.PendingPlaybackSamples(samplesWritten, headPosition);

        if (!_hasHeadBaseline)
        {
            _hasHeadBaseline = true;
            _lastHeadPosition = headPosition;
            if (pending >= _minPendingSamples)
            {
                _hasStallStart = true;
                _stallStartTicks = nowTicks;
            }
            return PlaybackStallDecision.None;
        }

        if ((uint)headPosition != (uint)_lastHeadPosition)
        {
            _lastHeadPosition = headPosition;
            _hasStallStart = false;
            return PlaybackStallDecision.None;
        }

        if (pending < _minPendingSamples)
        {
            _hasStallStart = false;
            return PlaybackStallDecision.None;
        }

        if (!_hasStallStart)
        {
            _hasStallStart = true;
            _stallStartTicks = nowTicks;
            return PlaybackStallDecision.None;
        }

        long stalledMs = nowTicks - _stallStartTicks;
        if (stalledMs < _stallThresholdMs) return PlaybackStallDecision.None;

        return TryRequestRebuild(nowTicks, $"playback head frozen at {headPosition} for {stalledMs} ms ({pending} samples pending)");
    }

    /// <summary>
    /// Observes the result of a device write alongside current progress.
    /// A negative return value requests an immediate rebuild (subject to cooldown); sustained zero writes request one too.
    /// </summary>
    public PlaybackStallDecision ObserveWrite(int written, long samplesWritten, int headPosition, long nowTicks)
    {
        if (written < 0)
        {
            _hasZeroWriteStart = false;
            return TryRequestRebuild(nowTicks, $"write failed ({written})");
        }

        if (written == 0)
        {
            if (!_hasZeroWriteStart)
            {
                _hasZeroWriteStart = true;
                _zeroWriteStartTicks = nowTicks;
            }
            else
            {
                long zeroMs = nowTicks - _zeroWriteStartTicks;
                if (zeroMs >= _zeroWriteStallMs)
                    return TryRequestRebuild(nowTicks, $"write returned 0 for {zeroMs} ms");
            }
        }
        else
        {
            _hasZeroWriteStart = false;
        }

        return ObserveProgress(samplesWritten, headPosition, nowTicks);
    }

    private PlaybackStallDecision TryRequestRebuild(long nowTicks, string reason)
    {
        if (_hasRebuilt && nowTicks - _lastRebuildTicks < _rebuildCooldownMs)
            return PlaybackStallDecision.None;

        _hasRebuilt = true;
        _lastRebuildTicks = nowTicks;
        Reset();
        return new PlaybackStallDecision(true, reason);
    }
}
