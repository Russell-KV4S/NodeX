using NodeX.UI;

namespace NodeX.Core.Tests;

public class DeepLinkForwarderTests
{
    private static string NewPipeName() => "nodex-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void TrySend_NoRunningInstance_ReturnsFalse()
    {
        Assert.False(DeepLinkForwarder.TrySend(NewPipeName(), "nodex://add?node=1999&host=club.local", TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task Server_ReceivesForwardedLinks_AndDropsAnythingElse()
    {
        string pipe = NewPipeName();
        var received = new List<string>();
        var gotSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var server = Task.Run(() => DeepLinkForwarder.RunServerAsync(pipe, link =>
        {
            lock (received) received.Add(link);
            if (link.Contains("node=2000")) gotSecond.TrySetResult();
        }, cts.Token));

        Assert.True(await Task.Run(() => DeepLinkForwarder.TrySend(pipe, "nodex://add?node=1999&host=club.local", TimeSpan.FromSeconds(3))));
        Assert.True(await Task.Run(() => DeepLinkForwarder.TrySend(pipe, "https://example.com/not-a-nodex-link", TimeSpan.FromSeconds(3))));
        Assert.True(await Task.Run(() => DeepLinkForwarder.TrySend(pipe, "nodex://add?node=2000&host=club.local", TimeSpan.FromSeconds(3))));
        await gotSecond.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cts.Cancel();
        await server.WaitAsync(TimeSpan.FromSeconds(5));

        lock (received)
            Assert.Equal(["nodex://add?node=1999&host=club.local", "nodex://add?node=2000&host=club.local"], received);
    }

    [Fact]
    public void TrySend_RefusesOverlongLink()
    {
        string link = "nodex://add?node=1999&host=" + new string('a', DeepLinkForwarder.MaxLinkLength);
        Assert.False(DeepLinkForwarder.TrySend(NewPipeName(), link, TimeSpan.FromMilliseconds(100)));
    }
}
