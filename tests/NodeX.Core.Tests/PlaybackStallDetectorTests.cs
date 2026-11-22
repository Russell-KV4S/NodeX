using NodeX.Core.Audio;

namespace NodeX.Core.Tests;

public sealed class PlaybackStallDetectorTests
{
    [Fact]
    public void NormalAdvancingPlayback_NeverRequestsRebuild()
    {
        var detector = new PlaybackStallDetector();
        long written = 0;
        int head = 0;

        for (long t = 0; t <= 5000; t += 20)
        {
            written += AudioFormat.SamplesPerFrame;
            var writeDecision = detector.ObserveWrite(AudioFormat.SamplesPerFrame, written, head, t);
            Assert.False(writeDecision.ShouldRebuild);

            if (t >= 40) head += AudioFormat.SamplesPerFrame; // normal ~40 ms hardware pipeline lead

            var progressDecision = detector.ObserveProgress(written, head, t + 10);
            Assert.False(progressDecision.ShouldRebuild);
        }
    }

    [Fact]
    public void FrozenHeadWithPendingBacklog_RequestsRebuildAfterThreshold_AndRespectsCooldown()
    {
        var detector = new PlaybackStallDetector(
            stallThresholdMs: 600,
            zeroWriteStallMs: 300,
            minPendingSamples: AudioFormat.SamplesPerFrame * 4,
            rebuildCooldownMs: 2000);

        long written = AudioFormat.SamplesPerFrame * 10; // 200 ms backlog
        const int frozenHead = 320;

        // Baseline established at t = 1000
        Assert.False(detector.ObserveProgress(written, frozenHead, 1000).ShouldRebuild);
        // Before 600 ms: still tolerated
        Assert.False(detector.ObserveProgress(written, frozenHead, 1580).ShouldRebuild);

        // Past 600 ms: rebuild triggered once
        var first = detector.ObserveProgress(written, frozenHead, 1620);
        Assert.True(first.ShouldRebuild);
        Assert.Contains("frozen", first.Reason, StringComparison.OrdinalIgnoreCase);

        // Still frozen inside cooldown (< 2000 ms after t=1620 -> t=3620): suppressed
        for (long t = 1640; t <= 3600; t += 100)
            Assert.False(detector.ObserveProgress(written, frozenHead, t).ShouldRebuild);

        // Once cooldown passes (and a new 600 ms stall completes after rebuild baseline at t=3600): rebuilds again
        var second = detector.ObserveProgress(written, frozenHead, 4250);
        Assert.True(second.ShouldRebuild);
    }

    [Fact]
    public void BriefStallFollowedByProgress_DoesNotRequestRebuild()
    {
        var detector = new PlaybackStallDetector(stallThresholdMs: 600);
        long written = AudioFormat.SamplesPerFrame * 6;

        Assert.False(detector.ObserveProgress(written, 0, 100).ShouldRebuild);
        Assert.False(detector.ObserveProgress(written, 0, 500).ShouldRebuild);
        // Hardware wakes up and advances
        Assert.False(detector.ObserveProgress(written, 160, 520).ShouldRebuild);
        // Another 400 ms pause from the new head does not add up to the first 400 ms
        Assert.False(detector.ObserveProgress(written, 160, 920).ShouldRebuild);
    }

    [Fact]
    public void NegativeWrite_TriggersImmediateRebuild()
    {
        var detector = new PlaybackStallDetector(rebuildCooldownMs: 2000);
        var decision = detector.ObserveWrite(-6, 1600, 1280, 500);
        Assert.True(decision.ShouldRebuild);
        Assert.Contains("-6", decision.Reason);
    }

    [Fact]
    public void SustainedZeroWrite_TriggersRebuildAfterZeroWriteThreshold()
    {
        var detector = new PlaybackStallDetector(stallThresholdMs: 600, zeroWriteStallMs: 300);

        Assert.False(detector.ObserveWrite(0, 1600, 1400, 1000).ShouldRebuild);
        Assert.False(detector.ObserveWrite(0, 1600, 1440, 1250).ShouldRebuild);

        var decision = detector.ObserveWrite(0, 1600, 1460, 1320);
        Assert.True(decision.ShouldRebuild);
        Assert.Contains("returned 0", decision.Reason);
    }

    [Fact]
    public void Reset_ClearsPendingStallTimers()
    {
        var detector = new PlaybackStallDetector(stallThresholdMs: 600);
        long written = AudioFormat.SamplesPerFrame * 6;

        Assert.False(detector.ObserveProgress(written, 0, 1000).ShouldRebuild);
        Assert.False(detector.ObserveProgress(written, 0, 1550).ShouldRebuild);

        detector.Reset();

        // New baseline at t=1560 means t=1620 is only 60 ms into the new track
        Assert.False(detector.ObserveProgress(written, 0, 1560).ShouldRebuild);
        Assert.False(detector.ObserveProgress(written, 0, 1620).ShouldRebuild);
    }

    [Fact]
    public void HeadWrapAround32Bit_AdvancesNormallyWithoutFalseStall()
    {
        var detector = new PlaybackStallDetector(stallThresholdMs: 600);
        long written = uint.MaxValue - 100L + 160;
        int headBefore = unchecked((int)(uint.MaxValue - 100u));
        int headWrapped = 60;

        Assert.False(detector.ObserveProgress(written, headBefore, 1000).ShouldRebuild);
        Assert.False(detector.ObserveProgress(written, headBefore, 1400).ShouldRebuild);
        // Wraps across 2^32
        Assert.False(detector.ObserveProgress(written, headWrapped, 1420).ShouldRebuild);
        Assert.False(detector.ObserveProgress(written, headWrapped, 1900).ShouldRebuild);
    }
}
