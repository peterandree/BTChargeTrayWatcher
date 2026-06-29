using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;

namespace BTChargeTrayWatcher;

internal static class StartupRegistration
{
    private const string AppName = "BTChargeTrayWatcher";
    private const string StartupTaskName = "BTChargeTrayWatcher Startup";
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedRunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                return IsRunKeyEnabled() || IsScheduledTaskEnabled();
            }
            catch { return false; }
        }
    }

    public static bool TryEnable(bool allowScheduledTaskFallback, out string? error)
    {
        error = null;

        if (TryEnableRunKey(out error))
        {
            return true;
        }

        if (!allowScheduledTaskFallback)
        {
            return false;
        }

        string? runKeyError = error;
        if (TryEnableScheduledTask(out string? taskError))
        {
            error = null;
            return true;
        }

        error = $"{runKeyError}{Environment.NewLine}Scheduled task fallback also failed: {taskError}";
        return false;
    }

    public static bool TryDisable(out string? error)
    {
        error = null;

        bool runKeyRemoved = TryDisableRunKey(out string? runKeyError);
        bool taskRemoved = TryDisableScheduledTask(out string? taskError);

        if (!runKeyRemoved && !taskRemoved)
        {
            error = $"Unable to disable startup: {runKeyError ?? "run key removal failed"}; {taskError ?? "scheduled task removal failed"}";
            return false;
        }

        if (IsEnabled)
        {
            error = "Windows still reports startup enabled after disable attempt.";
            return false;
        }

        return true;
    }

    public static StartupDiagnostics GetDiagnostics()
    {
        string? runValue = null;
        bool runKeyPathMatch = false;
        bool startupApprovedDisabled = false;
        bool scheduledTaskExists = false;
        bool scheduledTaskEnabled = false;
        string? scheduledTaskLastError = null;

        try
        {
            runValue = Registry.GetValue($"HKEY_CURRENT_USER\\{RunKey}", AppName, null) as string;
            runKeyPathMatch = IsExecutableCommandMatch(runValue, Application.ExecutablePath);
            startupApprovedDisabled = IsStartupApprovedDisabled(GetStartupApprovedData());
        }
        catch (Exception ex)
        {
            scheduledTaskLastError = ex.Message;
        }

        try
        {
            scheduledTaskExists = TryQueryScheduledTaskXml(out string? xml, out string? queryError);
            if (scheduledTaskExists && !string.IsNullOrWhiteSpace(xml))
            {
                scheduledTaskEnabled = ParseScheduledTaskEnabled(xml!);
            }
            else if (!scheduledTaskExists)
            {
                scheduledTaskEnabled = false;
            }

            if (!string.IsNullOrWhiteSpace(queryError))
            {
                scheduledTaskLastError = queryError;
            }
        }
        catch (Exception ex)
        {
            scheduledTaskLastError = ex.Message;
        }

        bool runKeyEnabled = runKeyPathMatch && !startupApprovedDisabled;
        bool effectiveEnabled = runKeyEnabled || (scheduledTaskExists && scheduledTaskEnabled);

        return new StartupDiagnostics(
            RunKeyValue: runValue,
            RunKeyPathMatchesExecutable: runKeyPathMatch,
            StartupApprovedDisabled: startupApprovedDisabled,
            ScheduledTaskExists: scheduledTaskExists,
            ScheduledTaskEnabled: scheduledTaskEnabled,
            EffectiveEnabled: effectiveEnabled,
            ScheduledTaskLastError: scheduledTaskLastError);
    }

    private static bool TryEnableRunKey(out string? error)
    {
        error = null;

        try
        {
            Registry.SetValue($"HKEY_CURRENT_USER\\{RunKey}", AppName, $"\"{Application.ExecutablePath}\"");
            ClearStartupApprovedValue();

            if (!IsRunKeyEnabled())
            {
                error = "Windows did not confirm Run-key startup registration. Your organization policy may block startup apps.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StartupRegistration] Enable RunKey Fault: {ex}");
            error = $"Unable to enable Run-key startup: {ex.Message}";
            return false;
        }
    }

    private static bool TryDisableRunKey(out string? error)
    {
        error = null;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            key?.DeleteValue(AppName, false);
            ClearStartupApprovedValue();

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StartupRegistration] Disable RunKey Fault: {ex}");
            error = $"Unable to disable Run-key startup: {ex.Message}";
            return false;
        }
    }

    private static bool IsRunKeyEnabled()
    {
        var value = Registry.GetValue($"HKEY_CURRENT_USER\\{RunKey}", AppName, null) as string;
        if (!IsExecutableCommandMatch(value, Application.ExecutablePath))
        {
            return false;
        }

        return !IsStartupApprovedDisabled(GetStartupApprovedData());
    }

    private static bool TryEnableScheduledTask(out string? error)
    {
        error = null;
        string escapedExecutable = Application.ExecutablePath.Replace("\"", "\"\"");
        string args = $"/Create /SC ONLOGON /TN \"{StartupTaskName}\" /TR \"\\\"{escapedExecutable}\\\"\" /F";

        if (!TryRunSchtasks(args, out int exitCode, out string output, out string stderr))
        {
            error = "Unable to invoke schtasks.exe. Startup fallback may be blocked by policy.";
            return false;
        }

        if (exitCode != 0)
        {
            error = BuildSchtasksError("create scheduled task", output, stderr, exitCode);
            return false;
        }

        if (!IsScheduledTaskEnabled())
        {
            error = "Scheduled task was created but is not enabled.";
            return false;
        }

        return true;
    }

    private static bool TryDisableScheduledTask(out string? error)
    {
        error = null;

        if (!TryQueryScheduledTaskXml(out _, out string? queryError))
        {
            return true;
        }

        string args = $"/Delete /TN \"{StartupTaskName}\" /F";
        if (!TryRunSchtasks(args, out int exitCode, out string output, out string stderr))
        {
            error = "Unable to invoke schtasks.exe to remove startup fallback task.";
            return false;
        }

        if (exitCode != 0)
        {
            error = BuildSchtasksError("delete scheduled task", output, stderr, exitCode);
            return false;
        }

        return true;
    }

    private static bool IsScheduledTaskEnabled()
    {
        if (!TryQueryScheduledTaskXml(out string? xml, out _))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(xml))
        {
            return false;
        }

        return ParseScheduledTaskEnabled(xml);
    }

    private static bool TryQueryScheduledTaskXml(out string? xml, out string? error)
    {
        xml = null;
        error = null;

        string args = $"/Query /TN \"{StartupTaskName}\" /XML";
        if (!TryRunSchtasks(args, out int exitCode, out string output, out string stderr))
        {
            error = "Unable to invoke schtasks.exe.";
            return false;
        }

        if (exitCode != 0)
        {
            return false;
        }

        xml = output;
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            error = stderr.Trim();
        }

        return true;
    }

    private static bool ParseScheduledTaskEnabled(string xml)
    {
        var document = XDocument.Parse(xml);
        XNamespace ns = document.Root?.Name.Namespace ?? XNamespace.None;
        string? enabledText = document.Descendants(ns + "Enabled").FirstOrDefault()?.Value;

        if (!bool.TryParse(enabledText, out bool enabled))
        {
            return true;
        }

        return enabled;
    }

    private static bool TryRunSchtasks(string arguments, out int exitCode, out string output, out string error)
    {
        exitCode = -1;
        output = string.Empty;
        error = string.Empty;

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            if (!process.Start())
            {
                return false;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WaitAll(stdoutTask, stderrTask);

            output = stdoutTask.Result;
            error = stderrTask.Result;
            exitCode = process.ExitCode;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StartupRegistration] schtasks fault: {ex}");
            error = ex.Message;
            return false;
        }
    }

    private static string BuildSchtasksError(string operation, string output, string stderr, int exitCode)
    {
        var builder = new StringBuilder();
        builder.Append($"Failed to {operation} (exit code {exitCode}).");

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            builder.Append(' ');
            builder.Append(stderr.Trim());
        }
        else if (!string.IsNullOrWhiteSpace(output))
        {
            builder.Append(' ');
            builder.Append(output.Trim());
        }

        return builder.ToString();
    }

    internal static bool IsExecutableCommandMatch(string? commandLine, string executablePath)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        if (!TryExtractExecutablePath(commandLine, out string? configuredPath))
        {
            return false;
        }

        string expected = NormalizePath(executablePath);
        string actual = NormalizePath(configuredPath);
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsStartupApprovedDisabled(byte[]? approvalValue)
    {
        if (approvalValue is null || approvalValue.Length == 0)
        {
            return false;
        }

        byte state = approvalValue[0];
        return state == 0x03 || state == 0x06;
    }

    private static byte[]? GetStartupApprovedData()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedRunKey, false);
        return key?.GetValue(AppName) as byte[];
    }

    private static void ClearStartupApprovedValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedRunKey, true);
        key?.DeleteValue(AppName, false);
    }

    private static bool TryExtractExecutablePath(string commandLine, out string executablePath)
    {
        commandLine = commandLine.Trim();
        executablePath = string.Empty;

        if (commandLine.Length == 0)
        {
            return false;
        }

        if (commandLine[0] == '"')
        {
            int closingQuote = commandLine.IndexOf('"', 1);
            if (closingQuote <= 1)
            {
                return false;
            }

            executablePath = commandLine.Substring(1, closingQuote - 1);
            return true;
        }

        int exeIndex = commandLine.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex <= 0)
        {
            return false;
        }

        executablePath = commandLine[..(exeIndex + 4)];
        return true;
    }

    private static string NormalizePath(string path)
    {
        string expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        return Path.GetFullPath(expanded);
    }
}

internal sealed record StartupDiagnostics(
    string? RunKeyValue,
    bool RunKeyPathMatchesExecutable,
    bool StartupApprovedDisabled,
    bool ScheduledTaskExists,
    bool ScheduledTaskEnabled,
    bool EffectiveEnabled,
    string? ScheduledTaskLastError);
