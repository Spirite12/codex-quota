using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using CodexQuota.Localization;

namespace CodexQuota.Uninstaller;

internal static class Program
{
    private const string CompanionProcessName = "codex-quota";
    private const string LauncherProcessName = "codex-quota-launcher";
    private const string ListenerTaskName = "CodexQuota-OnCodexStart";
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexQuota";
    private const string MarkerFileName = ".codex-quota-install";
    private const string CleanupArgument = "--cleanup";
    private const int CleanupAttempts = 10;

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 3 &&
            string.Equals(args[0], CleanupArgument, StringComparison.OrdinalIgnoreCase))
        {
            RunCleanupMode(args[1], args[2]);
            return;
        }

        var installRoot = ResolveInstallRoot();
        if (installRoot is null)
        {
            MessageBox.Show(
                UiText.T(
                    "无法确定 Codex Quota 的安装目录，卸载已取消。",
                    "Unable to determine the codex-quota installation directory. Uninstall canceled."),
                UiText.T("卸载 Codex Quota", "Uninstall codex-quota"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        if (!TryRunSafetyPrecheck(installRoot.FullName, out var safetyMessage))
        {
            MessageBox.Show(
                safetyMessage,
                UiText.T("卸载已取消", "Uninstall canceled"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var confirmation = MessageBox.Show(
            UiText.T(
                $"即将卸载 Codex Quota，并删除此安装目录中的所有文件：\n\n{installRoot.FullName}\n\n同时会删除 Codex 启动监听任务和“已安装的应用”条目。此操作无法撤销。是否继续？",
                $"This will uninstall codex-quota and delete all files in this installation directory:\n\n{installRoot.FullName}\n\nThe Codex startup listener task and Installed Apps entry will also be removed. This action cannot be undone. Continue?"),
            UiText.T("卸载 Codex Quota", "Uninstall codex-quota"),
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
                UiText.T("卸载未完成", "Uninstall incomplete"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        if (!TryRemoveListenerTask(out var taskError))
        {
            MessageBox.Show(
                taskError,
                UiText.T("卸载未完成", "Uninstall incomplete"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        if (!TryRemoveUninstallEntry(installRoot.FullName, out var registryError))
        {
            MessageBox.Show(
                registryError,
                UiText.T("卸载未完成", "Uninstall incomplete"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        var ready = MessageBox.Show(
            UiText.T(
                "监听任务和“已安装的应用”条目已删除。点击“确定”关闭卸载程序并删除整个安装目录。",
                "The listener task and Installed Apps entry have been removed. Click OK to close the uninstaller and delete the entire installation directory."),
            UiText.T("继续卸载", "Continue uninstall"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        if (ready != MessageBoxResult.OK)
        {
            return;
        }

        if (!ScheduleInstallRootDeletion(installRoot.FullName))
        {
            MessageBox.Show(
                UiText.T(
                    "无法安排删除安装目录，未删除任何文件。",
                    "Unable to schedule installation directory deletion. No files were deleted."),
                UiText.T("卸载未完成", "Uninstall incomplete"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static bool TryRunSafetyPrecheck(string installRoot, out string message)
    {
        var gitPath = Path.Combine(installRoot, ".git");
        if (Directory.Exists(gitPath) || File.Exists(gitPath))
        {
            message = UiText.T(
                "检测到当前目录是源码工程，不是安全的安装目录。\n\n" +
                "请从实际安装目录运行卸载程序。\n" +
                "本次卸载已取消，未修改任何内容。",
                "The current directory appears to be a source project, not a safe installation directory.\n\n" +
                "Run the uninstaller from the actual installation directory.\n" +
                "Uninstall canceled. No changes were made.");
            return false;
        }

        if (!TryFindExternalLockingProcesses(installRoot, out var processes, out var error))
        {
            message = UiText.T(
                $"无法确认卸载目录是否被占用。\n\n{error}\n\n" +
                "为安全起见，本次卸载已取消，未修改任何内容。",
                $"Unable to verify whether the uninstall directory is in use.\n\n{error}\n\n" +
                "For safety, uninstall canceled. No changes were made.");
            return false;
        }

        if (processes.Count == 0)
        {
            message = string.Empty;
            return true;
        }

        message = UiText.T(
            "检测到卸载目录正在被以下程序使用：\n\n" +
            string.Join("\n", processes) +
            "\n\n请关闭 Codex、资源管理器和终端后重试。\n" +
            "本次卸载已取消，未修改任何内容。",
            "The uninstall directory is being used by:\n\n" +
            string.Join("\n", processes) +
            "\n\nClose Codex, File Explorer, and Terminal, then try again.\n" +
            "Uninstall canceled. No changes were made.");
        return false;
    }

    private static bool TryFindExternalLockingProcesses(
        string installRoot,
        out List<string> processes,
        out string error)
    {
        processes = new List<string>();
        error = string.Empty;
        var resourceFiles = new List<string>();

        try
        {
            resourceFiles.AddRange(Directory.EnumerateFiles(
                installRoot,
                "*",
                SearchOption.AllDirectories));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = exception.Message;
            return false;
        }

        if (resourceFiles.Count == 0)
        {
            return true;
        }

        var sessionKey = Guid.NewGuid().ToString("N");
        var startResult = RmStartSession(out var sessionHandle, 0, sessionKey);
        if (startResult != ErrorSuccess)
        {
            error = UiText.T(
                $"Restart Manager 错误：{startResult}。",
                $"Restart Manager error: {startResult}.");
            return false;
        }

        try
        {
            var registerResult = RmRegisterResources(
                sessionHandle,
                (uint)resourceFiles.Count,
                resourceFiles.ToArray(),
                0,
                nint.Zero,
                0,
                null);
            if (registerResult != ErrorSuccess)
            {
                error = UiText.T(
                    $"Restart Manager 错误：{registerResult}。",
                    $"Restart Manager error: {registerResult}.");
                return false;
            }

            uint processInfoNeeded;
            uint processInfoCount = 0;
            uint rebootReasons;
            var listResult = RmGetList(
                sessionHandle,
                out processInfoNeeded,
                ref processInfoCount,
                Array.Empty<RmProcessInfo>(),
                out rebootReasons);
            if (listResult == ErrorSuccess)
            {
                return true;
            }

            if (listResult != ErrorMoreData || processInfoNeeded == 0)
            {
                error = UiText.T(
                    $"Restart Manager 错误：{listResult}。",
                    $"Restart Manager error: {listResult}.");
                return false;
            }

            var affectedProcesses = new RmProcessInfo[processInfoNeeded];
            processInfoCount = processInfoNeeded;
            listResult = RmGetList(
                sessionHandle,
                out processInfoNeeded,
                ref processInfoCount,
                affectedProcesses,
                out rebootReasons);
            if (listResult != ErrorSuccess)
            {
                error = UiText.T(
                    $"Restart Manager 错误：{listResult}。",
                    $"Restart Manager error: {listResult}.");
                return false;
            }

            foreach (var affectedProcess in affectedProcesses.Take((int)processInfoCount))
            {
                if (affectedProcess.ProcessId == Environment.ProcessId ||
                    IsManagedProcess(affectedProcess.ProcessId, installRoot))
                {
                    continue;
                }

                processes.Add(DescribeProcess(affectedProcess));
            }

            return true;
        }
        finally
        {
            RmEndSession(sessionHandle);
        }
    }

    private static bool IsManagedProcess(int processId, string installRoot)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var executablePath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executablePath) ||
                !IsPathWithinInstallRoot(executablePath, installRoot))
            {
                return false;
            }

            var executableName = Path.GetFileNameWithoutExtension(executablePath);
            return string.Equals(executableName, "codex-quota", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(executableName, "codex-quota-launcher", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string DescribeProcess(RmProcessInfo process)
    {
        var appName = string.IsNullOrWhiteSpace(process.AppName)
            ? UiText.T("未知程序", "Unknown process")
            : process.AppName.Trim();
        return UiText.T(
            $"{appName}（PID {process.ProcessId}）",
            $"{appName} (PID {process.ProcessId})");
    }

    private static bool IsPathWithinInstallRoot(string path, string installRoot)
    {
        var fullPath = Path.GetFullPath(path);
        var rootWithSeparator = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar) +
                                Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
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
                        error = UiText.T(
                            $"无法停止正在运行的 Codex Quota 进程：{ex.Message}",
                            $"Unable to stop a running codex-quota process: {ex.Message}");
                        return false;
                    }

                    if (!process.HasExited)
                    {
                        error = UiText.T(
                            "Codex Quota 进程仍在运行，未删除任何文件。请关闭它后重试。",
                            "A codex-quota process is still running. No files were deleted. Close it and try again.");
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

            error = UiText.T(
                $"无法检查 Codex 启动监听任务，未删除任何文件。\n\n{queryOutput.Trim()}",
                $"Unable to inspect the Codex startup listener task. No files were deleted.\n\n{queryOutput.Trim()}");
            return false;
        }

        var deletion = RunSchtasks("/Delete", "/TN", ListenerTaskName, "/F");
        if (deletion.ExitCode == 0)
        {
            error = string.Empty;
            return true;
        }

        var output = $"{deletion.StandardOutput}\n{deletion.StandardError}".Trim();
        error = UiText.T(
            $"无法删除 Codex 启动监听任务，未删除任何文件。\n\n{output}",
            $"Unable to remove the Codex startup listener task. No files were deleted.\n\n{output}");
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
                    error = UiText.T(
                        "“已安装的应用”条目指向其他安装文件夹，未删除任何文件。",
                        "The Installed Apps entry points to a different installation folder. No files were deleted.");
                    return false;
                }
            }

            Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            error = UiText.T(
                $"无法删除 Windows“已安装的应用”条目，未删除任何文件。\n\n{exception.Message}",
                $"Unable to remove the Windows Installed Apps entry. No files were deleted.\n\n{exception.Message}");
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
            return new ProcessResult(
                -1,
                string.Empty,
                UiText.T("无法启动 schtasks.exe。", "Unable to start schtasks.exe"));
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
        var currentExecutable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExecutable) || !File.Exists(currentExecutable))
        {
            return false;
        }

        var cleanupExecutable = Path.Combine(
            Path.GetTempPath(),
            $"codex-quota-uninstaller-cleanup-{Guid.NewGuid():N}.exe");

        try
        {
            File.Copy(currentExecutable, cleanupExecutable);

            var startInfo = new ProcessStartInfo
            {
                FileName = cleanupExecutable,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath()
            };
            startInfo.ArgumentList.Add(CleanupArgument);
            startInfo.ArgumentList.Add(installRoot);
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());

            using var process = Process.Start(startInfo);
            if (process is not null)
            {
                return true;
            }
        }
        catch
        {
        }

        try
        {
            File.Delete(cleanupExecutable);
        }
        catch
        {
        }

        return false;
    }

    private static void RunCleanupMode(string installRoot, string parentProcessIdText)
    {
        if (!int.TryParse(parentProcessIdText, out var parentProcessId) ||
            !IsSafeInstallRootForCleanup(installRoot))
        {
            return;
        }

        WaitForParentProcess(parentProcessId);

        for (var attempt = 0; attempt < CleanupAttempts; attempt++)
        {
            if (!Directory.Exists(installRoot))
            {
                break;
            }

            try
            {
                Directory.Delete(installRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            if (!Directory.Exists(installRoot))
            {
                break;
            }

            Thread.Sleep(1000);
        }

        var cleanupExecutable = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(cleanupExecutable))
        {
            ScheduleFileDeletion(cleanupExecutable);
        }
    }

    private static bool IsSafeInstallRootForCleanup(string installRoot)
    {
        try
        {
            var fullPath = Path.GetFullPath(installRoot);
            var pathRoot = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(pathRoot) || PathsEqual(fullPath, pathRoot))
            {
                return false;
            }

            if (Directory.Exists(Path.Combine(fullPath, ".git")) ||
                File.Exists(Path.Combine(fullPath, ".git")))
            {
                return false;
            }

            return File.Exists(Path.Combine(fullPath, MarkerFileName)) &&
                   (HasInstallationFiles(fullPath, useLegacyDist: false) ||
                    HasInstallationFiles(fullPath, useLegacyDist: true));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void WaitForParentProcess(int parentProcessId)
    {
        if (parentProcessId <= 0 || parentProcessId == Environment.ProcessId)
        {
            Thread.Sleep(1000);
            return;
        }

        try
        {
            using var parent = Process.GetProcessById(parentProcessId);
            if (!parent.HasExited)
            {
                parent.WaitForExit(10000);
            }
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static void ScheduleFileDeletion(string filePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath()
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            "Start-Sleep -Milliseconds 1000; " +
            "Remove-Item -LiteralPath $env:CODEX_QUOTA_CLEANUP_FILE -Force -ErrorAction SilentlyContinue");
        startInfo.Environment["CODEX_QUOTA_CLEANUP_FILE"] = filePath;

        try
        {
            using var process = Process.Start(startInfo);
        }
        catch
        {
        }
    }

    private const int ErrorSuccess = 0;
    private const int ErrorMoreData = 234;

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(
        out uint sessionHandle,
        int sessionFlags,
        string sessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint sessionHandle,
        uint fileCount,
        [In, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] fileNames,
        uint applicationCount,
        nint applications,
        uint serviceCount,
        [In, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[]? serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint sessionHandle,
        out uint processInfoNeeded,
        ref uint processInfoCount,
        [In, Out] RmProcessInfo[] processInfo,
        out uint rebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint sessionHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string AppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string ServiceName;

        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Restartable;

        public int ProcessId => Process.ProcessId;
    }

    private readonly record struct ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
