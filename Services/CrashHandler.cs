using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace VRCNext.Services;

/// <summary>
/// Global crash handler — catches unhandled exceptions and writes detailed
/// crash reports to %AppData%/VRCNext/Logs/Crashes/crash_yyyy-MM-dd_HH-mm-ss.txt
/// </summary>
internal static class CrashHandler
{
    private static string _crashDir = "";

    public static void Register()
    {
        _crashDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VRCNext", "Logs", "Crashes");
        Directory.CreateDirectory(_crashDir);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            WriteCrashReport(e.ExceptionObject as Exception, "AppDomain.UnhandledException", e.IsTerminating);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashReport(e.Exception, "TaskScheduler.UnobservedTaskException", isTerminating: false);
            e.SetObserved();
        };
    }

    private static void WriteCrashReport(Exception? ex, string source, bool isTerminating)
    {
        try
        {
            var timestamp = DateTime.Now;
            var fileName = $"crash_{timestamp:yyyy-MM-dd_HH-mm-ss}.txt";
            var path = Path.Combine(_crashDir, fileName);

            var sb = new StringBuilder();

            // Header
            sb.AppendLine("═══════════════════════════════════════════════════════════════");
            sb.AppendLine("                    VRCNext Crash Report");
            sb.AppendLine("═══════════════════════════════════════════════════════════════");
            sb.AppendLine();

            // Timestamp & source
            sb.AppendLine($"Timestamp (local) : {timestamp:yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"Timestamp (UTC)   : {timestamp.ToUniversalTime():yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"Source            : {source}");
            sb.AppendLine($"Is Terminating    : {isTerminating}");
            sb.AppendLine();

            // Application info
            sb.AppendLine("─── Application ───────────────────────────────────────────────");
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            sb.AppendLine($"Name              : {asm.GetName().Name}");
            sb.AppendLine($"Version           : {asm.GetName().Version}");
            sb.AppendLine($"Informational     : {asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "N/A"}");
            sb.AppendLine($"Location          : {Environment.ProcessPath ?? asm.Location}");
            sb.AppendLine($"Working Dir       : {Environment.CurrentDirectory}");
            sb.AppendLine($"Command Line      : {Environment.CommandLine}");
            sb.AppendLine($"Process ID        : {Environment.ProcessId}");
            sb.AppendLine($"Uptime            : {(DateTime.Now - Process.GetCurrentProcess().StartTime):hh\\:mm\\:ss}");
            sb.AppendLine();

            // Runtime info
            sb.AppendLine("─── Runtime ───────────────────────────────────────────────────");
            sb.AppendLine($".NET Version      : {RuntimeInformation.FrameworkDescription}");
            sb.AppendLine($"Runtime ID        : {RuntimeInformation.RuntimeIdentifier}");
            sb.AppendLine($"Architecture      : {RuntimeInformation.ProcessArchitecture}");
            sb.AppendLine($"Debug Build       : {Debugger.IsAttached}");
            sb.AppendLine();

            // OS info
            sb.AppendLine("─── System ────────────────────────────────────────────────────");
            sb.AppendLine($"OS                : {RuntimeInformation.OSDescription}");
            sb.AppendLine($"OS Architecture   : {RuntimeInformation.OSArchitecture}");
            sb.AppendLine($"Machine Name      : {Environment.MachineName}");
            sb.AppendLine($"User Name         : {Environment.UserName}");
            sb.AppendLine($"Processors        : {Environment.ProcessorCount}");
            sb.AppendLine();

            // Memory
            sb.AppendLine("─── Memory ────────────────────────────────────────────────────");
            var proc = Process.GetCurrentProcess();
            sb.AppendLine($"Working Set       : {FormatBytes(proc.WorkingSet64)}");
            sb.AppendLine($"Private Memory    : {FormatBytes(proc.PrivateMemorySize64)}");
            sb.AppendLine($"GC Total Memory   : {FormatBytes(GC.GetTotalMemory(false))}");
            sb.AppendLine($"GC Gen0 Count     : {GC.CollectionCount(0)}");
            sb.AppendLine($"GC Gen1 Count     : {GC.CollectionCount(1)}");
            sb.AppendLine($"GC Gen2 Count     : {GC.CollectionCount(2)}");
            sb.AppendLine($"Thread Count      : {proc.Threads.Count}");
            sb.AppendLine();

            // Exception chain
            sb.AppendLine("─── Exception ─────────────────────────────────────────────────");
            if (ex != null)
            {
                WriteException(sb, ex, depth: 0);
            }
            else
            {
                sb.AppendLine("(No exception object available)");
            }
            sb.AppendLine();

            // Loaded assemblies
            sb.AppendLine("─── Loaded Assemblies ─────────────────────────────────────────");
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies()
                         .OrderBy(a => a.GetName().Name))
            {
                var name = a.GetName();
                sb.AppendLine($"  {name.Name,-45} {name.Version}");
            }
            sb.AppendLine();

            sb.AppendLine("═══════════════════════════════════════════════════════════════");
            sb.AppendLine("                      End of Crash Report");
            sb.AppendLine("═══════════════════════════════════════════════════════════════");

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Last resort — if the crash report itself fails, there's nothing more we can do
        }
    }

    private static void WriteException(StringBuilder sb, Exception ex, int depth)
    {
        var prefix = depth == 0 ? "" : $"[Inner Exception {depth}] ";

        sb.AppendLine($"{prefix}Type              : {ex.GetType().FullName}");
        sb.AppendLine($"{prefix}Message           : {ex.Message}");
        if (ex.HResult != 0)
            sb.AppendLine($"{prefix}HResult           : 0x{ex.HResult:X8}");
        if (!string.IsNullOrEmpty(ex.Source))
            sb.AppendLine($"{prefix}Source            : {ex.Source}");
        if (ex.TargetSite != null)
            sb.AppendLine($"{prefix}Target Site       : {ex.TargetSite.DeclaringType?.FullName}.{ex.TargetSite.Name}");

        // Extra data on special exception types
        if (ex is AggregateException agg)
        {
            sb.AppendLine($"{prefix}Inner Exceptions  : {agg.InnerExceptions.Count}");
        }
        if (ex.Data.Count > 0)
        {
            sb.AppendLine($"{prefix}Data              :");
            foreach (System.Collections.DictionaryEntry entry in ex.Data)
                sb.AppendLine($"  {entry.Key} = {entry.Value}");
        }

        sb.AppendLine($"{prefix}Stack Trace       :");
        if (!string.IsNullOrEmpty(ex.StackTrace))
        {
            foreach (var line in ex.StackTrace.Split('\n'))
                sb.AppendLine($"  {line.TrimEnd()}");
        }
        else
        {
            sb.AppendLine("  (No stack trace available)");
        }

        // Recurse into inner exceptions
        if (ex is AggregateException aggEx)
        {
            foreach (var inner in aggEx.InnerExceptions)
            {
                sb.AppendLine();
                WriteException(sb, inner, depth + 1);
            }
        }
        else if (ex.InnerException != null)
        {
            sb.AppendLine();
            WriteException(sb, ex.InnerException, depth + 1);
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB"];
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]} ({bytes:N0} bytes)";
    }
}
