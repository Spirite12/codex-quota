using System.Diagnostics;
using System.Runtime.InteropServices;
using CodexQuota.Localization;
using CodexQuota.RuntimeSupport;

namespace CodexQuota.Bootstrapper;

internal static class Program
{
    private const uint MessageBoxOk = 0x00000000;
    private const uint MessageBoxIconInformation = 0x00000040;
    private const uint MessageBoxIconError = 0x00000010;
    private const uint WmClose = 0x0010;
    private const int StartupMessageDurationMilliseconds = 1800;
    private const string BootstrapperMutexName = "CodexQuota.Bootstrapper";
    private static readonly string StartupMessageCaption =
        UiText.T("Codex Quota - 正在启动", "Codex Quota - Starting");

    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, BootstrapperMutexName, out var createdNew);
        if (!createdNew)
        {
            ShowMessage(
                UiText.T(
                    "Codex Quota 已经在启动中，请稍候。",
                    "Codex Quota is already starting. Please wait."),
                "Codex Quota",
                MessageBoxIconInformation);
            return;
        }

        if (!RuntimeRequirement.IsDesktopRuntimeAvailable())
        {
            RuntimeRequirement.ShowMissingRuntimeMessage();
            return;
        }

        var runtimeDirectory = Path.Combine(AppContext.BaseDirectory, "runtime");
        var companionPath = Path.Combine(runtimeDirectory, "codex-quota.exe");
        if (!File.Exists(companionPath))
        {
            ShowMessage(
                UiText.T(
                    "未找到 Companion 文件，请重新安装 Codex Quota。",
                    "The Companion file was not found. Please reinstall Codex Quota."),
                "Codex Quota",
                MessageBoxIconError);
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = companionPath,
                WorkingDirectory = runtimeDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var argument in args)
            {
                startInfo.ArgumentList.Add(argument);
            }

            Process.Start(startInfo);
            ShowTransientMessage(
                UiText.T(
                    "Codex Quota 正在启动，请稍候……",
                    "Codex Quota is starting. Please wait..."),
                StartupMessageCaption);
        }
        catch (Exception exception)
        {
            ShowMessage(
                UiText.T(
                    $"Codex Quota 启动失败。\n\n{exception.Message}",
                    $"Codex Quota failed to start.\n\n{exception.Message}"),
                "Codex Quota",
                MessageBoxIconError);
        }
    }

    private static void ShowTransientMessage(string message, string caption)
    {
        var closeThread = new Thread(() =>
        {
            Thread.Sleep(StartupMessageDurationMilliseconds);
            var window = FindWindow(null, caption);
            if (window != nint.Zero)
            {
                PostMessage(window, WmClose, nint.Zero, nint.Zero);
            }
        })
        {
            IsBackground = true
        };
        closeThread.Start();
        ShowMessage(message, caption, MessageBoxIconInformation);
    }

    private static void ShowMessage(string message, string caption, uint icon)
    {
        MessageBox(nint.Zero, message, caption, MessageBoxOk | icon);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(nint hWnd, string text, string caption, uint type);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")]
    private static extern nint FindWindow(string? className, string windowName);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hWnd, uint message, nint wParam, nint lParam);
}
