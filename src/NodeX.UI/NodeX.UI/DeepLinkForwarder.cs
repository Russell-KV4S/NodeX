using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NodeX.UI;

/// <summary>
/// Hands a <c>nodex://</c> link from a second desktop launch (the OS starts a new process when the user clicks the
/// link) to the NodeX that is already running, so that one copy owns the node registration (§4.9). A local named
/// pipe restricted to the current user carries one line of text; the receiver only ever passes it to
/// <see cref="AppServices.DeliverDeepLink"/>, which opens the confirmation form and never saves or connects.
/// </summary>
public static class DeepLinkForwarder
{
    /// <summary>Longest link accepted over the pipe; anything longer is ignored.</summary>
    public const int MaxLinkLength = 2048;

    /// <summary>Per-user pipe name for the desktop app.</summary>
    public static string DefaultPipeName => "NodeX.UI.Desktop.links." + SafeUserName();

    /// <summary>
    /// Sends <paramref name="link"/> to a running instance. Returns false when no instance answers within
    /// <paramref name="timeout"/> (the caller then starts normally and handles the link itself).
    /// </summary>
    public static bool TrySend(string pipeName, string link, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(link) || link.Length > MaxLinkLength) return false;
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect((int)Math.Max(1, timeout.TotalMilliseconds));
            byte[] bytes = Encoding.UTF8.GetBytes(link.Trim() + "\n");
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Listens for forwarded links until <paramref name="cancellationToken"/> is cancelled. Each line that starts
    /// with <c>nodex://</c> is passed to <paramref name="onLink"/>; anything else is dropped.
    /// </summary>
    public static async Task RunServerAsync(string pipeName, Action<string> onLink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onLink);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readCts.CancelAfter(TimeSpan.FromSeconds(5));
                string? line = await ReadLineAsync(server, readCts.Token).ConfigureAwait(false);
                if (line != null && line.StartsWith("nodex://", StringComparison.OrdinalIgnoreCase))
                    onLink(line);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                // A client that vanished or sent nothing in time, or another NodeX copy already owns the pipe
                // ("all pipe instances are busy"); wait so neither case can spin.
                try { await Task.Delay(1000, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxLinkLength + 2];
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
            int nl = Array.IndexOf(buffer, (byte)'\n', 0, total);
            if (nl >= 0) { total = nl; break; }
        }
        if (total == 0 || total > MaxLinkLength) return null;
        return Encoding.UTF8.GetString(buffer, 0, total).Trim();
    }

    private static string SafeUserName()
    {
        var sb = new StringBuilder();
        foreach (char ch in Environment.UserName ?? "user")
            sb.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '_');
        return sb.Length > 0 ? sb.ToString() : "user";
    }
}
