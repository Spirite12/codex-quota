using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using FormsDialogResult = System.Windows.Forms.DialogResult;
using FormsFolderBrowserDialog = System.Windows.Forms.FolderBrowserDialog;
using WpfMessageBox = System.Windows.MessageBox;

namespace CodexQuota.Installer;

internal static class Program
{
    private const string ListenerTaskName = "CodexQuota-OnCodexStart";
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexQuota";
    private const string MarkerFileName = ".codex-quota-install";
    private static readonly byte[] PayloadMagic = Encoding.ASCII.GetBytes("CODEXQUOTA_PAYLOAD_V1");

    [STAThread]
    private static void Main()
    {
        string? payloadPath = null;
        try
        {
            payloadPath = ExtractEmbeddedPayload();
            if (payloadPath is null)
            {
                ShowError(
                    "This file does not contain a valid codex-quota installation package.",
                    "Install codex-quota");
                return;
            }

            var defaultPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexQuota");
            Directory.CreateDirectory(defaultPath);

            string installRoot;
            using (var dialog = new FormsFolderBrowserDialog
            {
                Description = "Choose the installation folder for codex-quota.",
                SelectedPath = defaultPath,
                ShowNewFolderButton = true,
                UseDescriptionForTitle = true
            })
            {
                if (dialog.ShowDialog() != FormsDialogResult.OK)
                {
                    return;
                }

                installRoot = Path.GetFullPath(dialog.SelectedPath);
            }

            if (!ValidateInstallRoot(installRoot, out var validationError))
            {
                ShowError(validationError, "Install codex-quota");
                return;
            }

            if (!ConfirmInstallation(installRoot))
            {
                return;
            }

            Directory.CreateDirectory(installRoot);
            ZipFile.ExtractToDirectory(payloadPath, installRoot, overwriteFiles: true);
            File.WriteAllText(
                Path.Combine(installRoot, MarkerFileName),
                "Codex Quota installation marker\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (!TryRegisterUninstallEntry(installRoot, out var registryError))
            {
                ShowError(registryError, "Install incomplete");
                return;
            }

            if (!TryRegisterListenerTask(installRoot, out var taskError))
            {
                TryRemoveUninstallEntry(installRoot, out _);
                ShowError(taskError, "Install incomplete");
                return;
            }

            var runResult = RunSchtasks("/Run", "/TN", ListenerTaskName);
            var message = runResult.ExitCode == 0
                ? "codex-quota was installed. The listener is running and will start the Companion when Codex is open."
                : "codex-quota was installed. The listener will start automatically at the next sign-in.";
            WpfMessageBox.Show(
                message,
                "codex-quota installed",
                MessageBoxButton.OK,
                runResult.ExitCode == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception exception)
        {
            ShowError(
                $"Installation failed.\n\n{exception.Message}",
                "Install incomplete");
        }
        finally
        {
            if (payloadPath is not null)
            {
                try
                {
                    File.Delete(payloadPath);
                }
                catch
                {
                }
            }
        }
    }

    private static bool ConfirmInstallation(string installRoot)
    {
        var existingInstallation = IsExistingInstallation(installRoot);
        var action = existingInstallation ? "update" : "install";
        var message = $"This will {action} codex-quota in:\n\n{installRoot}\n\nThe Windows startup task and Installed Apps entry will also be configured. Continue?";
        return WpfMessageBox.Show(
            message,
            "Install codex-quota",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private static bool ValidateInstallRoot(string installRoot, out string error)
    {
        error = string.Empty;
        var setupPath = Environment.ProcessPath;
        var setupDirectory = setupPath is null ? null : Path.GetDirectoryName(Path.GetFullPath(setupPath));
        if (setupDirectory is not null && PathsEqual(installRoot, setupDirectory))
        {
            error = "Choose a folder different from the folder containing this setup file.";
            return false;
        }

        var pathRoot = Path.GetPathRoot(installRoot);
        if (pathRoot is not null && PathsEqual(installRoot, pathRoot))
        {
            error = "Choose a dedicated installation folder, not a drive root.";
            return false;
        }

        if (!Directory.Exists(installRoot))
        {
            return true;
        }

        var existingInstallation = IsExistingInstallation(installRoot);
        if (existingInstallation)
        {
            return true;
        }

        if (Directory.EnumerateFileSystemEntries(installRoot).Any())
        {
            error = "Choose an empty folder or an existing codex-quota installation folder.";
            return false;
        }

        return true;
    }

    private static bool TryRegisterUninstallEntry(string installRoot, out string error)
    {
        try
        {
            var uninstallerPath = Path.Combine(installRoot, "uninstall-codex-quota.exe");
            using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath);
            if (key is null)
            {
                error = "Unable to create the Windows Installed Apps entry.";
                return false;
            }

            key.SetValue("DisplayName", "Codex Quota", RegistryValueKind.String);
            key.SetValue("DisplayVersion", "0.1.0", RegistryValueKind.String);
            key.SetValue("Publisher", "Codex Quota", RegistryValueKind.String);
            key.SetValue("InstallLocation", installRoot, RegistryValueKind.String);
            key.SetValue("UninstallString", $"\"{uninstallerPath}\"", RegistryValueKind.String);
            key.SetValue("DisplayIcon", uninstallerPath, RegistryValueKind.String);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            error = $"Unable to create the Windows Installed Apps entry.\n\n{exception.Message}";
            return false;
        }
    }

    private static bool TryRegisterListenerTask(string installRoot, out string error)
    {
        var launcherPath = Path.Combine(installRoot, "codex-quota-launcher.exe");
        if (!File.Exists(launcherPath))
        {
            error = "The launcher file is missing from the installation package.";
            return false;
        }

        var currentUser = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var result = RunSchtasks(
            "/Create",
            "/TN",
            ListenerTaskName,
            "/SC",
            "ONLOGON",
            "/RU",
            currentUser,
            "/RL",
            "LIMITED",
            "/IT",
            "/TR",
            $"\"{launcherPath}\"",
            "/F");
        if (result.ExitCode == 0)
        {
            error = string.Empty;
            return true;
        }

        error = $"Unable to create the Codex startup task.\n\n{FormatProcessOutput(result)}";
        return false;
    }

    private static bool IsExistingInstallation(string installRoot)
    {
        return File.Exists(Path.Combine(installRoot, MarkerFileName)) ||
               HasExecutableLayout(installRoot) ||
               HasExecutableLayout(Path.Combine(installRoot, "dist"));
    }

    private static bool HasExecutableLayout(string executableRoot)
    {
        return File.Exists(Path.Combine(executableRoot, "codex-quota.exe")) &&
               File.Exists(Path.Combine(executableRoot, "codex-quota-launcher.exe")) &&
               File.Exists(Path.Combine(executableRoot, "uninstall-codex-quota.exe"));
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

    private static string? ExtractEmbeddedPayload()
    {
        var setupPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(setupPath) || !File.Exists(setupPath))
        {
            return null;
        }

        using var input = new FileStream(setupPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var footerLength = sizeof(long) + PayloadMagic.Length;
        if (input.Length < footerLength)
        {
            return null;
        }

        input.Seek(-footerLength, SeekOrigin.End);
        var lengthBytes = new byte[sizeof(long)];
        var magicBytes = new byte[PayloadMagic.Length];
        ReadExactly(input, lengthBytes);
        ReadExactly(input, magicBytes);
        if (!magicBytes.SequenceEqual(PayloadMagic))
        {
            return null;
        }

        var payloadLength = BitConverter.ToInt64(lengthBytes, 0);
        var payloadStart = input.Length - footerLength - payloadLength;
        if (payloadLength <= 0 || payloadStart < 0)
        {
            return null;
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"codex-quota-payload-{Guid.NewGuid():N}.zip");
        input.Seek(payloadStart, SeekOrigin.Begin);
        using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            CopyExactly(input, output, payloadLength);
        }

        return tempPath;
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

    private static string FormatProcessOutput(ProcessResult result)
    {
        return $"{result.StandardOutput}\n{result.StandardError}".Trim();
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static void ReadExactly(Stream input, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = input.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }
    }

    private static void CopyExactly(Stream input, Stream output, long length)
    {
        var buffer = new byte[1024 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            output.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static void ShowError(string message, string title)
    {
        WpfMessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private readonly record struct ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
