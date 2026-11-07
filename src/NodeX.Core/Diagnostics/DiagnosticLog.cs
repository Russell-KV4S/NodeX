using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NodeX.Core.Diagnostics;

/// <summary>
/// A small rolling log file for troubleshooting away from a computer (e.g. a Wi-Fi to cellular handoff test),
/// written alongside the on-screen event log. Lines carry a full date and millisecond timestamp. <see cref="Info"/>
/// lines are always written; <see cref="Detail"/> lines only while <see cref="Detailed"/> is on. Secrets are
/// scrubbed from every line (see <see cref="Redact"/>), and callers must still never pass one in.
/// Logging never throws: a disk problem must not take the app down.
/// </summary>
public sealed class DiagnosticLog
{
    /// <summary>The application-wide log. Inert until <see cref="Configure"/> gives it a folder.</summary>
    public static DiagnosticLog Default { get; } = new();

    private const string BaseName = "nodex";
    private readonly object _lock = new();
    private string? _directory;
    private long _maxFileBytes = 512 * 1024;
    private int _keepFiles = 3;
    private long _currentSize;

    /// <summary>Also write <see cref="Detail"/> lines (per-step timings, protocol state). Off by default: it is chatty.</summary>
    public volatile bool Detailed;

    public bool IsConfigured => _directory != null;

    /// <summary>The folder holding the log files, or null when not configured.</summary>
    public string? Directory => _directory;

    /// <summary>Path of the live log file, or null when not configured.</summary>
    public string? CurrentFile => _directory == null ? null : Path.Combine(_directory, BaseName + ".log");

    /// <summary>
    /// Starts logging into <paramref name="directory"/> (created if needed). Files roll when one reaches
    /// <paramref name="maxFileBytes"/>; at most <paramref name="keepFiles"/> are kept (the live one plus older ones).
    /// </summary>
    public void Configure(string directory, long maxFileBytes = 512 * 1024, int keepFiles = 3)
    {
        lock (_lock)
        {
            try
            {
                System.IO.Directory.CreateDirectory(directory);
                _directory = directory;
                _maxFileBytes = Math.Max(1024, maxFileBytes);
                _keepFiles = Math.Max(1, keepFiles);
                string file = CurrentFile!;
                _currentSize = File.Exists(file) ? new FileInfo(file).Length : 0;
            }
            catch (Exception)
            {
                _directory = null;
            }
        }
    }

    /// <summary>Writes a line that is always kept, e.g. an event the user also sees on screen.</summary>
    public void Info(string category, string message) => Write("INFO", category, message);

    /// <summary>Writes a line only when <see cref="Detailed"/> is on.</summary>
    public void Detail(string category, string message)
    {
        if (Detailed) Write("DBG ", category, message);
    }

    /// <summary>Writes a session header (version, platform) so a shared log says what produced it.</summary>
    public void WriteHeader(string appVersion)
    {
        Info("app", $"==== NodeX {appVersion} | {System.Runtime.InteropServices.RuntimeInformation.OSDescription} | " +
                    $"{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture} | detailed={(Detailed ? "on" : "off")} ====");
    }

    private void Write(string level, string category, string message)
    {
        if (_directory == null) return;

        string line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} [{category}] {Redact(message)}{Environment.NewLine}");

        lock (_lock)
        {
            if (_directory == null) return;
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(line);
                if (_currentSize + bytes.Length > _maxFileBytes) Roll();
                using (var fs = new FileStream(CurrentFile!, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    fs.Write(bytes, 0, bytes.Length);
                }
                _currentSize += bytes.Length;
            }
            catch (Exception)
            {
                // Out of space, read-only storage, antivirus lock...: drop the line.
            }
        }
    }

    /// <summary>nodex.log → nodex.1.log → nodex.2.log; the oldest beyond <c>_keepFiles</c> is deleted. Caller holds the lock.</summary>
    private void Roll()
    {
        string dir = _directory!;
        for (int i = _keepFiles - 1; i >= 1; i--)
        {
            string from = i == 1 ? CurrentFile! : Path.Combine(dir, $"{BaseName}.{i - 1}.log");
            string to = Path.Combine(dir, $"{BaseName}.{i}.log");
            if (!File.Exists(from)) continue;
            try { File.Move(from, to, overwrite: true); } catch (IOException) { }
        }

        if (_keepFiles == 1)
        {
            try { File.Delete(CurrentFile!); } catch (IOException) { }
        }
        _currentSize = 0;
    }

    /// <summary>The log files, oldest first (so reading them in order reads in time order).</summary>
    public IReadOnlyList<string> GetFiles()
    {
        if (_directory == null) return [];
        var files = new List<string>();
        for (int i = _keepFiles - 1; i >= 1; i--)
        {
            string old = Path.Combine(_directory, $"{BaseName}.{i}.log");
            if (File.Exists(old)) files.Add(old);
        }
        if (File.Exists(CurrentFile!)) files.Add(CurrentFile!);
        return files;
    }

    /// <summary>
    /// All retained log text in time order, capped to the most recent <paramref name="maxChars"/> characters (an
    /// e-mail body and an Android intent both have size limits). Empty when there is nothing logged yet.
    /// </summary>
    public string ReadAll(int maxChars = 200_000)
    {
        lock (_lock)
        {
            var sb = new StringBuilder();
            foreach (string file in GetFiles())
            {
                try { sb.Append(File.ReadAllText(file, Encoding.UTF8)); } catch (IOException) { }
            }

            string text = sb.ToString();
            if (text.Length <= maxChars) return text;

            // Keep the newest part, starting at a line boundary.
            string tail = text[^maxChars..];
            int nl = tail.IndexOf('\n');
            return "[earlier lines omitted]" + Environment.NewLine + (nl >= 0 ? tail[(nl + 1)..] : tail);
        }
    }

    /// <summary>Writes the retained log, capped as in <see cref="ReadAll"/>, to one new file for sharing; null if nothing to share.</summary>
    public string? CreateShareFile(int maxChars = 2_000_000)
    {
        if (_directory == null) return null;
        string text = ReadAll(maxChars);
        if (text.Length == 0) return null;

        string path = Path.Combine(_directory, $"nodex-log-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt");
        try
        {
            File.WriteAllText(path, text, new UTF8Encoding(false));
            // Share copies pile up otherwise.
            foreach (string stale in System.IO.Directory.GetFiles(_directory, "nodex-log-*.txt").OrderDescending().Skip(3))
            {
                try { File.Delete(stale); } catch (IOException) { }
            }
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Belt and braces: callers never log secrets, but anything shaped like one is scrubbed anyway.
    private static readonly Regex SecretPattern = new(
        @"(?i)\b(pass(?:word|wd)?|secret|token|authorization|apikey|api[_-]key)\b(\s*[=:]\s*)(""[^""]*""|\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Replaces the value in <c>password=...</c>, <c>token: ...</c> and the like with <c>***</c>, and flattens control characters.</summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var sb = new StringBuilder(text.Length);
        foreach (char ch in text)
        {
            if (ch == '\0') continue;
            sb.Append(char.IsControl(ch) ? ' ' : ch);
        }

        return SecretPattern.Replace(sb.ToString(), m => m.Groups[1].Value + m.Groups[2].Value + "***");
    }
}
