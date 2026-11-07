using System.Text.RegularExpressions;
using NodeX.Core.Diagnostics;
using Xunit;

namespace NodeX.Core.Tests;

public sealed class DiagnosticLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nodex-diag-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private DiagnosticLog Create(long maxBytes = 512 * 1024, int keep = 3)
    {
        var log = new DiagnosticLog();
        log.Configure(_dir, maxBytes, keep);
        return log;
    }

    [Fact]
    public void Unconfigured_log_ignores_writes_and_never_throws()
    {
        var log = new DiagnosticLog();
        log.Info("x", "hello");
        log.Detail("x", "hello");
        Assert.False(log.IsConfigured);
        Assert.Empty(log.GetFiles());
        Assert.Equal("", log.ReadAll());
        Assert.Null(log.CreateShareFile());
    }

    [Fact]
    public void Lines_have_date_millisecond_time_level_and_category()
    {
        var log = Create();
        log.Info("net", "network changed");

        string text = File.ReadAllText(log.CurrentFile!);
        Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} INFO \[net\] network changed\r?\n$"), text);
    }

    [Fact]
    public void Detail_lines_are_written_only_when_detailed_logging_is_on()
    {
        var log = Create();
        log.Detail("iax", "off-line");
        Assert.Equal("", log.ReadAll());

        log.Detailed = true;
        log.Detail("iax", "on-line");
        string text = log.ReadAll();
        Assert.Contains("on-line", text);
        Assert.DoesNotContain("off-line", text);
        Assert.Contains("DBG", text);
    }

    [Theory]
    [InlineData("login password=hunter2 ok", "hunter2")]
    [InlineData("Password: hunter2", "hunter2")]
    [InlineData("token=abc123def", "abc123def")]
    [InlineData("secret = \"topsecretvalue\"", "topsecretvalue")]
    public void Secrets_are_redacted(string message, string leaked)
    {
        string redacted = DiagnosticLog.Redact(message);
        Assert.Contains("***", redacted);
        Assert.DoesNotContain(leaked, redacted);
    }

    [Fact]
    public void Control_characters_are_flattened_so_a_line_stays_one_line()
    {
        Assert.Equal("a b c", DiagnosticLog.Redact("a\nb\0\tc").Replace("  ", " "));
        var log = Create();
        log.Info("x", "line1\r\nline2");
        Assert.Single(File.ReadAllLines(log.CurrentFile!));
    }

    [Fact]
    public void Log_rolls_at_the_size_cap_and_keeps_only_the_configured_number_of_files()
    {
        var log = Create(maxBytes: 2048, keep: 3);
        for (int i = 0; i < 200; i++) log.Info("fill", $"line {i:000} " + new string('x', 60));

        var files = log.GetFiles();
        Assert.Equal(3, files.Count);
        Assert.All(files, f => Assert.True(new FileInfo(f).Length <= 2048 + 200));
        Assert.Equal(3, Directory.GetFiles(_dir, "nodex*.log").Length);

        // Oldest first, and the very newest line is in the live file.
        string all = log.ReadAll();
        Assert.Contains("line 199", all);
        Assert.True(all.IndexOf("line 150", StringComparison.Ordinal) < all.IndexOf("line 199", StringComparison.Ordinal));
        Assert.DoesNotContain("line 000", all);
    }

    [Fact]
    public void ReadAll_keeps_the_newest_text_when_capped()
    {
        var log = Create();
        for (int i = 0; i < 100; i++) log.Info("x", $"entry {i:000}");

        string tail = log.ReadAll(maxChars: 600);
        Assert.StartsWith("[earlier lines omitted]", tail);
        Assert.Contains("entry 099", tail);
        Assert.DoesNotContain("entry 000", tail);
    }

    [Fact]
    public void CreateShareFile_writes_one_file_and_prunes_old_copies()
    {
        var log = Create();
        log.Info("x", "something");

        var paths = new List<string>();
        for (int i = 0; i < 6; i++)
        {
            string? p = log.CreateShareFile();
            Assert.NotNull(p);
            paths.Add(p!);
            Assert.Contains("something", File.ReadAllText(p!));
            Thread.Sleep(5);      // names carry milliseconds
        }

        Assert.True(Directory.GetFiles(_dir, "nodex-log-*.txt").Length <= 4);
        Assert.True(File.Exists(paths[^1]));
    }

    [Fact]
    public async Task Concurrent_writers_do_not_lose_or_interleave_lines()
    {
        var log = Create();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < 100; i++) log.Info("t" + t, "msg " + i);
        })));

        var lines = log.ReadAll().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(800, lines.Length);
        Assert.All(lines, l => Assert.Matches(@"INFO \[t\d\] msg \d+\r?$", l));
    }
}
