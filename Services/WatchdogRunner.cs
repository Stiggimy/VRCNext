using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;

namespace VRCNext.Services;

// Watchdog mode — runs as a detached child process, monitors the main VRCNext PID for Crash handling. Only a Test ver rn.
internal static class WatchdogRunner
{
    public static void Run(string[] args)
    {
        if (args.Length < 4) return;
        if (!int.TryParse(args[1], out var targetPid)) return;
        var sentinelPath = args[2];
        var crashDir     = args[3];

        int exitCode = 0;
        try
        {
            var proc = Process.GetProcessById(targetPid);
            proc.WaitForExit();
            try { exitCode = proc.ExitCode; } catch { }
        }
        catch (ArgumentException) { /* already gone — exit code unknown, treat as clean */ }
        catch { return; }
        if (!File.Exists(sentinelPath)) return;

        // exitCode >= 0 is deliberate termination eg taskkill /F is 1, clean close is 0, VSCode restart is prob. 0/1).
        if (exitCode >= 0) return;

        string sentinelContent = "";
        try { sentinelContent = File.ReadAllText(sentinelPath, Encoding.UTF8); } catch { }
        try { File.Delete(sentinelPath); } catch { }

        var stderrPath = ParseSentinelField(sentinelContent, "Stderr");

        try
        {
            Directory.CreateDirectory(crashDir);
            var timestamp = DateTime.Now;
            var path      = Path.Combine(crashDir, $"crash_{timestamp:yyyy-MM-dd_HH-mm-ss}_unclean.txt");

            var sb = new StringBuilder();
            sb.AppendLine("═══════════════════════════════════════════════════════════════");
            sb.AppendLine("             VRCNext Watchdog Crash Report");
            sb.AppendLine("═══════════════════════════════════════════════════════════════");
            sb.AppendLine();
            sb.AppendLine("The monitored process exited without a clean-shutdown marker.");
            sb.AppendLine("No managed exception handler was reached.  Likely causes:");
            sb.AppendLine("  • StackOverflowException  — CLR kills the process before any handler runs");
            sb.AppendLine("  • Native crash in Photino or a P/Invoke call  (e.g. 0xC0000005 AV)");
            sb.AppendLine("  • OutOfMemoryException on a very large allocation");
            sb.AppendLine("  • Process killed via Task Manager / Windows OOM");
            sb.AppendLine("  • Environment.FailFast() called");
            sb.AppendLine();
            sb.AppendLine($"Crash detected at : {timestamp:yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"Watchdog PID      : {Environment.ProcessId}");
            sb.AppendLine();

            sb.AppendLine("─── Crashed Session ────────────────────────────────────────────");
            sb.AppendLine(sentinelContent.TrimEnd());
            sb.AppendLine();

            AppendStderrFile(sb, stderrPath);
            AppendWerEventLog(sb, targetPid);

            sb.AppendLine("─── Watchdog Environment ───────────────────────────────────────");
            sb.AppendLine($".NET Version : {RuntimeInformation.FrameworkDescription}");
            sb.AppendLine($"OS           : {RuntimeInformation.OSDescription}");
            sb.AppendLine();
            sb.AppendLine("═══════════════════════════════════════════════════════════════");
            sb.AppendLine("                   End of Watchdog Report");
            sb.AppendLine("═══════════════════════════════════════════════════════════════");

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);

            // Send anonymous crash data to developer Discord webhook (if user has not opted out)
            SendToDiscordAsync(sentinelContent, sb.ToString()).GetAwaiter().GetResult();
        }
        catch { }

        // Restart after crash, only for native crashes (exitCode < 0 = Windows exception code like 0xC0000005)
        // Still sending reports in case of an crash lol
        try
        {
            if (exitCode < 0
                && IsRestartAfterCrashEnabled()
                && UptimeSeconds(sentinelContent) >= 30)
            {
                var exePath = ParseSentinelField(sentinelContent, "Exe");
                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                {
                    Thread.Sleep(2000);
                    Process.Start(new ProcessStartInfo { FileName = exePath, UseShellExecute = true });
                }
            }
        }
        catch { }
    }

    // ── Discord webhook ────────────────────────────────────────────────────

    private const string WebhookUrl =
        "https://discord.com/api/webhooks/1491312855969169408/BHNBz7Ke4p8NYXH2x-pIyj6MwaMhCh6hKJKxTRiaF9aI7HeVf6ebMQ1Wsx3AJLH7pYv7";

    private static async Task SendToDiscordAsync(string sentinelContent, string fullReport)
    {
        try
        {
            // Check user opt-out: read settings.json from AppData
            if (!IsCrashReportingEnabled()) return;

            // Extract only the two useful sections — strip everything else
            var payload = ExtractAndSanitize(fullReport);
            if (string.IsNullOrWhiteSpace(payload)) return;

            // Header line: version + .NET + OS (no paths, no usernames)
            var version = ParseSentinelField(sentinelContent, "Version");
            var dotnet  = RuntimeInformation.FrameworkDescription;
            var os      = RuntimeInformation.OSDescription;
            var header  = $"**VRCNext crash** | v{version} | {dotnet} | {os}";

            using var http    = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var form    = new MultipartFormDataContent();
            var payloadBytes  = Encoding.UTF8.GetBytes(payload);
            var fileContent   = new ByteArrayContent(payloadBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

            form.Add(new StringContent(header),  "content");
            form.Add(fileContent, "files[0]", $"crash_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt");

            await http.PostAsync(WebhookUrl, form);
        }
        catch { /* never let webhook failure affect crash reporting or app startup */ }
    }

    private static bool IsCrashReportingEnabled()
    {
        try
        {
            var settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VRCNext", "settings.json");
            if (!File.Exists(settingsPath)) return true; // default on

            var json = File.ReadAllText(settingsPath, Encoding.UTF8);
            // Quick parse — avoid pulling in Newtonsoft in the watchdog hot path
            // Property can be "SendCrashData":false or "sendCrashData":false
            var match = RxSendCrashData.Match(json);
            if (match.Success)
                return match.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        catch { }
        return true; // default on
    }

    private static bool IsRestartAfterCrashEnabled()
    {
        try
        {
            var settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VRCNext", "settings.json");
            if (!File.Exists(settingsPath)) return true; // default on

            var json = File.ReadAllText(settingsPath, Encoding.UTF8);
            var match = RxRestartAfterCrash.Match(json);
            if (match.Success)
                return match.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        catch { }
        return true; // default on
    }

    /// <summary>Returns how many seconds the crashed session had been running (from sentinel's Started field).</summary>
    private static int UptimeSeconds(string sentinelContent)
    {
        try
        {
            var started = ParseSentinelField(sentinelContent, "Started");
            if (DateTime.TryParse(started, out var t))
                return (int)(DateTime.Now - t).TotalSeconds;
        }
        catch { }
        return int.MaxValue; // unknown → assume long uptime, allow restart
    }

    /// <summary>
    /// Extracts only the CLR stderr and Event Log sections from the full report,
    /// then strips all Windows paths so no personal data is sent.
    /// </summary>
    private static string ExtractAndSanitize(string report)
    {
        var sb = new StringBuilder();

        // Pull out the two sections we want
        sb.Append(ExtractSection(report, "─── CLR stderr"));
        sb.Append(ExtractSection(report, "─── Windows Application Event Log"));

        var result = sb.ToString();
        if (string.IsNullOrWhiteSpace(result)) return result;

        // Strip Windows absolute paths — these may contain the username (C:\Users\<name>\...)
        result = RxWindowsPath.Replace(result, "<path-redacted>");

        // Strip Faulting process id / fault offset / start time / report id (not useful, potentially identifying)
        result = RxFaultPid.Replace(result, "$1<pid>");
        result = RxFaultOffset.Replace(result, "$1<offset>");
        result = RxFaultTime.Replace(result, "$1<ts>");
        result = RxReportId.Replace(result, "$1<id>");

        return result;
    }

    /// <summary>Extracts one section from the report, from its header line to the next separator line.</summary>
    private static string ExtractSection(string report, string sectionHeader)
    {
        var start = report.IndexOf(sectionHeader, StringComparison.Ordinal);
        if (start < 0) return "";

        // Next section starts at the next "─── " line after start, or end of report
        var nextSection = report.IndexOf("\n─── ", start + sectionHeader.Length, StringComparison.Ordinal);
        var end = nextSection >= 0 ? nextSection + 1 : report.Length;

        return report[start..end].TrimEnd() + "\n\n";
    }

    private static string ParseSentinelField(string sentinel, string fieldName)
    {
        foreach (var raw in sentinel.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var key = fieldName + "    :";
            if (line.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                return line[key.Length..].Trim();
            var idx = line.IndexOf(':');
            if (idx > 0 && line[..idx].Trim().Equals(fieldName, StringComparison.OrdinalIgnoreCase))
                return line[(idx + 1)..].Trim();
        }
        return "";
    }

    private static void AppendStderrFile(StringBuilder sb, string stderrPath)
    {
        sb.AppendLine("─── CLR stderr  (native crash output — untruncated) ─────────────");

        if (string.IsNullOrEmpty(stderrPath) || !File.Exists(stderrPath))
        {
            sb.AppendLine("  (stderr file not found — crash happened before CrashHandler.Register())");
            sb.AppendLine();
            return;
        }

        try
        {
            using var fs      = new FileStream(stderrPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader  = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var content = reader.ReadToEnd();

            if (string.IsNullOrWhiteSpace(content))
                sb.AppendLine("  (empty — crash produced no stderr output; may be OOM or external kill)");
            else
                sb.Append(content);  
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  (could not read stderr file: {ex.Message})");
        }
        finally
        {
            try { File.Delete(stderrPath); } catch { }
        }

        sb.AppendLine();
    }

    private static void AppendWerEventLog(StringBuilder sb, int pid)
    {
#if WINDOWS
        try
        {
            sb.AppendLine("─── Windows Application Event Log ──────────────────────────────");
            using var log = new System.Diagnostics.EventLog("Application");
            var entries = log.Entries.Cast<EventLogEntry>()
                .Where(e => e.EntryType == EventLogEntryType.Error
                         && (e.Source == "Application Error" || e.Source == ".NET Runtime"
                             || e.Source == "Windows Error Reporting")
                         && e.TimeGenerated >= DateTime.Now.AddMinutes(-10))
                .OrderByDescending(e => e.TimeGenerated)
                .Take(5)
                .ToList();

            if (entries.Count == 0)
            {
                sb.AppendLine("  (no recent error events found)");
            }
            else
            {
                foreach (var e in entries)
                {
                    sb.AppendLine($"[{e.TimeGenerated:HH:mm:ss}] Source: {e.Source}  EventID: {e.EventID}");
                    foreach (var line in e.Message.Split('\n'))
                    {
                        var trimmed = line.TrimEnd('\r');
                        if (!string.IsNullOrWhiteSpace(trimmed))
                            sb.AppendLine($"  {trimmed}");
                    }
                    sb.AppendLine();
                }
            }
        }
        catch
        {
            sb.AppendLine("  (event log access denied or unavailable)");
        }
        sb.AppendLine();
#endif
    }

    // ── Compiled regexes ──────────────────────────────────────────────────
    private static readonly System.Text.RegularExpressions.Regex RxSendCrashData =
        new(@"""[Ss]end[Cc]rash[Dd]ata""\s*:\s*(true|false)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex RxRestartAfterCrash =
        new(@"""[Rr]estart[Aa]fter[Cc]rash""\s*:\s*(true|false)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex RxWindowsPath =
        new(@"[A-Za-z]:\\[^\s\n,""']+", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex RxFaultPid =
        new(@"(Faulting process id:\s*)0x[0-9a-fA-F]+", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex RxFaultOffset =
        new(@"(Fault offset:\s*)0x[0-9a-fA-F]+", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex RxFaultTime =
        new(@"(Faulting application start time:\s*)0x[0-9a-fA-F]+", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex RxReportId =
        new(@"(Report Id:\s*)[0-9a-f\-]+", System.Text.RegularExpressions.RegexOptions.Compiled);
}