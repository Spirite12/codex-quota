using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace CodexQuota.Uninstaller;

internal static class Program
{
    private const string CompanionProcessName = "codex-quota";
    private const string LauncherProcessName = "codex-quota-launcher";
    private const string ListenerTaskName = "CodexQuota-OnCodexStart";
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexQuota";
    private const string MarkerFileName = ".codex-quota-install";

    [STAThread]
    private static void Main()
    {
        var installRoot = ResolveInstallRoot();
        if (installRoot is null)
        {
            MessageBox.Show(
                "Unable to determine the codex-quota installation directory. Uninstall canceled.",
                "Uninstall codex-quota",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        var confirmation = MessageBox.Show(
            $"This will uninstall codex-quota and delete all files in this installation directory:\n\n{installRoot.FullName}\n\nThe Codex startup listener task and Installed Apps entry will also be removed. This action cannot be undone. Continue?",
            "Uninstall codex-quota",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        if (!TryStopManagedProcesses(installRoot.FullName, out var stopError))
        {
            MessageBox.Show(
                stopError,
                "Uninstall incomplete",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        if (!TryRemoveListenerTask(out var taskError))
        {
            MessageBox.Show(
                taskError,
                "Uninstall incomplete",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        if (!TryRemoveUninstallEntry(installRoot.FullName, out var registryError))
        {
            MessageBox.Show(
                registryError,
                "Uninstall incomplete",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        var ready = MessageBox.Show(
            "The listener task and Installed Apps entry have been removed. Click OK to close the uninstaller and delete the entire installation directory.",
            "Continue uninstall",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        if (ready != MessageBoxResult.OK)
        {
            return;
        }

        if (!ScheduleInstallRootDeletion(installRoot.FullName))
        {
            MessageBox.Show(
                "Unable to schedule installation directory deletion. No files were deleted.",
                "Uninstall incomplete",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static DirectoryInfo? ResolveInstallRoot()
    {
        var executableDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        if (HasInstallationFiles(executableDirectory.FullName, useLegacyDist: false) &&
            (File.Exists(Path.Combine(executableDirectory.FullName, MarkerFileName)) ||
             string.Equals(executableDirectory.Name, "codex-quota", StringComparison.OrdinalIgnoreCase)))
        {
            return executableDirectory;
        }

        if (string.Equals(executableDirectory.Name, "dist", StringComparison.OrdinalIgnoreCase) &&
            executableDirectory.Parent is not null &&
            executableDirectory.Parent.Exists &&
            HasInstallationFiles(executableDirectory.Parent.FullName, useLegacyDist: true))
        {
            var legacyInstallRoot = executableDirectory.Parent;
            var markerPath = Path.Combine(legacyInstallRoot.FullName, MarkerFileName);
            var legacyInstallPath = string.Equals(legacyInstallRoot.Name, "codex-quota", StringComparison.OrdinalIgnoreCase);
            return File.Exists(markerPath) || legacyInstallPath ? legacyInstallRoot : null;
        }

        return null;
    }

    private static bool HasInstallationFiles(string installRoot, bool useLegacyDist)
    {
        var executableRoot = useLegacyDist ? Path.Combine(installRoot, "dist") : installRoot;
        return File.Exists(Path.Combine(executableRoot, "codex-quota.exe")) &&
               File.Exists(Path.Combine(executableRoot, "codex-quota-launcher.exe")) &&
               File.Exists(Path.Combine(executableRoot, "uninstall-codex-quota.exe"));
    }

    private static bool TryStopManagedProcesses(string installRoot, out string error)
    {
        foreach (var processName in new[] { LauncherProcessName, CompanionProcessName })
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    string? executablePath;
                    try
                    {
                        executablePath = process.MainModule?.FileName;
                    }
                    catch (Exception) when (process.HasExited)
                    {
                        continue;
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(executablePath) ||
                        !IsPathWithinInstallRoot(executablePath, installRoot, processName))
                    {
                        continue;
                    }

                    try
                    {
                        if (!process.HasExited)
                        {
                            process.CloseMainWindow();
                            if (!process.WaitForExit(3000) && !process.HasExited)
                            {
                                process.Kill();
                                process.WaitForExit(3000);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        error = $"Unable to stop a running codex-quota process: {ex.Message}";
                        return false;
                    }

                    if (!process.HasExited)
                    {
                        error = "A codex-quota process is still running. No files were deleted. Close it and try again.";
                        return false;
                    }
                }
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool IsPathWithinInstallRoot(string path, string installRoot, string processName)
    {
        var fullPath = Path.GetFullPath(path);
        var rootWithSeparator = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(Path.GetFileNameWithoutExtension(fullPath), processName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryRemoveListenerTask(out string error)
    {
        var query = RunSchtasks("/Query", "/TN", ListenerTaskName);
        if (query.ExitCode != 0)
        {
            var queryOutput = $"{query.StandardOutput}\n{query.StandardError}";
            if (LooksLikeMissingTask(queryOutput))
            {
                error = string.Empty;
                return true;
            }

            error = $"Unable to inspect the Codex startup listener task. No files were deleted.\n\n{queryOutput.Trim()}";
            return false;
        }

        var deletion = RunSchtasks("/Delete", "/TN", ListenerTaskName, "/F");
        if (deletion.ExitCode == 0)
        {
            error = string.Empty;
            return true;
        }

        error = $"Unable to remove the Codex startup listener task. No files were deleted.\n\n{deletion.StandardOutput}\n{deletion.StandardError}".Trim();
        return false;
    }

    private static bool TryRemoveUninstallEntry(string installRoot, out string error)
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath, writable: false))
            {
                if (key is null)
                {
                    error = string.Empty;
                    return true;
                }

                var registeredRoot = key.GetValue("InstallLocation") as string;
                if (!string.IsNullOrWhiteSpace(registeredRoot) && !PathsEqual(registeredRoot, installRoot))
                {
                    error = "The Installed Apps entry points to a different installation folder. No files were deleted.";
                    return false;
                }
            }

            Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            error = $"Unable to remove the Windows Installed Apps entry. No files were deleted.\n\n{exception.Message}";
            return false;
        }
    }

    private static ProcessResult RunSchtasks(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return new ProcessResult(-1, string.Empty, "Unable to start schtasks.exe");
        }

        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit(5000);
        return new ProcessResult(process.HasExited ? process.ExitCode : -1, standardOutput, standardError);
    }

    private static bool LooksLikeMissingTask(string output)
    {
        return output.Contains("cannot find", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("specified file", StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool ScheduleInstallRootDeletion(string installRoot)
    {
        if (installRoot.IndexOfAny(new[] { '&', '|', '<', '>', '^', '%' }) >= 0)
        {
            return false;
        }

        var commandShell = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        var startInfo = new ProcessStartInfo
        {
            FileName = commandShell,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath()
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add($"timeout /t 2 /nobreak >nul & rmdir /s /q \"{installRoot}\"");
        return Process.Start(startInfo) is not null;
    }

    private readonly record struct ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
