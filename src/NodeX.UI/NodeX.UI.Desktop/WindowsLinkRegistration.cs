using System;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace NodeX.UI.Desktop;

/// <summary>
/// Registers this exe as the per-user handler for <c>nodex://</c> links (§4.9 share links) under
/// <c>HKEY_CURRENT_USER\Software\Classes\nodex</c>. Per-user, so no administrator rights; written only when the
/// stored command differs (the copy of NodeX that ran last owns the links). Failures are logged and ignored.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsLinkRegistration
{
    private const string KeyPath = @"Software\Classes\nodex";

    public static void EnsureRegistered()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
            // "dotnet NodeX.UI.Desktop.dll" (tests, tools) must not take over the links.
            if (System.IO.Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return;

            string command = $"\"{exe}\" \"%1\"";
            using (var existing = Registry.CurrentUser.OpenSubKey(KeyPath + @"\shell\open\command"))
            {
                if (string.Equals(existing?.GetValue(null) as string, command, StringComparison.OrdinalIgnoreCase)) return;
            }

            using var root = Registry.CurrentUser.CreateSubKey(KeyPath);
            root.SetValue(null, "URL:NodeX private node link");
            root.SetValue("URL Protocol", "");
            using (var icon = root.CreateSubKey("DefaultIcon")) icon.SetValue(null, $"\"{exe}\",0");
            using (var cmd = root.CreateSubKey(@"shell\open\command")) cmd.SetValue(null, command);
            NodeX.Core.Diagnostics.DiagnosticLog.Default.Info("links", $"registered nodex:// links to {exe}");
        }
        catch (Exception ex)
        {
            NodeX.Core.Diagnostics.DiagnosticLog.Default.Info("links", "could not register nodex:// links: " + ex.Message);
        }
    }
}
