using System.Diagnostics;
using CodexQuota.RuntimeSupport;

namespace CodexQuota.Launcher;

internal static class Program
{
    private const string DesktopProcessName = "ChatGPT";
    private const string CompanionProcessName = "codex-quota";
    private const string CompanionFileName = "codex-quota.exe";
    private const string RuntimeDirectoryName = "runtime";
    private const int PollMilliseconds = 1000;

    private static async Task Main()
    {
        if (!RuntimeRequirement.IsDesktopRuntimeAvailable())
        {
            RuntimeRequirement.ShowMissingRuntimeMessage();
            return;
        }

        var installRoot = ResolveInstallRoot();
        if (installRoot is null)
        {
            return;
        }

        using var mutex = new Mutex(true, "CodexQuota.Launcher", out var createdNew);
        if (!createdNew)
        {
            return;
        }

        var companionPath = ResolveCompanionPath(installRoot);
        if (companionPath is null)
        {
            return;
        }

        while (true)
        {
            try
            {
                if (IsCodexDesktopRunning())
                {
                    EnsureCompanionStarted(companionPath, installRoot.FullName);
                }
                else
                {
                    StopCompanions(installRoot.FullName);
                }
            }
            catch
            {
            }

            await Task.Delay(PollMilliseconds);
        }
    }

    private static DirectoryInfo? ResolveInstallRoot()
    {
        var executableDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        DirectoryInfo? installRoot = null;

        if (string.Equals(executableDirectory.Name, "dist", StringComparison.OrdinalIgnoreCase) &&
            executableDirectory.Parent is not null &&
            File.Exists(Path.Combine(executableDirectory.FullName, CompanionFileName)))
        {
            installRoot = executableDirectory.Parent;
        }
        else if (File.Exists(Path.Combine(executableDirectory.FullName, CompanionFileName)))
        {
            installRoot = executableDirectory;
        }

        if (installRoot is null || !installRoot.Exists)
        {
            return null;
        }

        return ResolveCompanionPath(installRoot) is not null ? installRoot : null;
    }

    private static string? ResolveCompanionPath(DirectoryInfo installRoot)
    {
        var runtimePath = Path.Combine(installRoot.FullName, RuntimeDirectoryName, CompanionFileName);
        if (File.Exists(runtimePath))
        {
            return runtimePath;
        }

        var currentPath = Path.Combine(installRoot.FullName, CompanionFileName);
        if (File.Exists(currentPath))
        {
            return currentPath;
        }

        var legacyPath = Path.Combine(installRoot.FullName, "dist", CompanionFileName);
        return File.Exists(legacyPath) ? legacyPath : null;
    }

    private static bool IsCodexDesktopRunning()
    {
        try
        {
            return Process.GetProcessesByName(DesktopProcessName).Any(process =>
            {
                using (process)
                {
                    return !process.HasExited;
                }
            });
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureCompanionStarted(string companionPath, string installRoot)
    {
        if (!File.Exists(companionPath))
        {
            return;
        }

        var existingCompanions = FindManagedProcesses(installRoot);
        if (existingCompanions.Count > 0)
        {
            foreach (var process in existingCompanions)
            {
                process.Dispose();
            }

            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = companionPath,
                WorkingDirectory = Path.GetDirectoryName(companionPath) ?? installRoot,
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch
        {
        }
    }

    private static void StopCompanions(string installRoot)
    {
        foreach (var process in FindManagedProcesses(installRoot))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.CloseMainWindow();
                        if (!process.WaitForExit(1500) && !process.HasExited)
                        {
                            process.Kill();
                            process.WaitForExit(1500);
                        }
                    }
                }
                catch
                {
                }
            }
        }
    }

    private static List<Process> FindManagedProcesses(string installRoot)
    {
        var processes = new List<Process>();
        foreach (var process in Process.GetProcessesByName(CompanionProcessName))
        {
            try
            {
                var executablePath = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(executablePath) && IsCompanionPath(executablePath, installRoot))
                {
                    processes.Add(process);
                }
                else
                {
                    process.Dispose();
                }
            }
            catch
            {
                process.Dispose();
            }
        }

        return processes;
    }

    private static bool IsCompanionPath(string path, string installRoot)
    {
        var fullPath = Path.GetFullPath(path);
        var rootWithSeparator = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var runtimePath = Path.GetFullPath(Path.Combine(installRoot, RuntimeDirectoryName, CompanionFileName));
        if (File.Exists(runtimePath))
        {
            return string.Equals(fullPath, runtimePath, StringComparison.OrdinalIgnoreCase);
        }

        return fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(Path.GetFileName(fullPath), CompanionFileName, StringComparison.OrdinalIgnoreCase);
    }
}
