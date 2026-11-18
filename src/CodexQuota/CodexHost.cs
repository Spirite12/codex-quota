using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;

namespace CodexQuota;

internal readonly record struct HostBounds(int Left, int Top, int Right, int Bottom)
{
    public nint WindowHandle { get; init; }
    public Rect? SidebarAnchorRectPixels { get; init; }
    public bool HasModalDialog { get; init; }

    public long Area => (long)Math.Max(0, Right - Left) * Math.Max(0, Bottom - Top);
}

internal static class CodexHost
{
    private const string DesktopProcessName = "ChatGPT";
    private const string CodexProcessName = "codex";
    private const string CodexHostProcessName = "codex-code-mode-host";
    private const uint GwHwndPrev = 3;

    public static bool IsCodexRuntimePresent()
    {
        return Process.GetProcessesByName(CodexProcessName).Length > 0 || Process.GetProcessesByName(CodexHostProcessName).Length > 0;
    }

    public static string ResolveCodexExecutablePath()
    {
        foreach (var processName in new[] { CodexProcessName, CodexHostProcessName })
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        var executablePath = process.MainModule?.FileName;
                        if (string.IsNullOrWhiteSpace(executablePath))
                        {
                            continue;
                        }

                        if (string.Equals(processName, CodexProcessName, StringComparison.OrdinalIgnoreCase))
                        {
                            return executablePath;
                        }

                        var siblingCodexPath = Path.Combine(
                            Path.GetDirectoryName(executablePath) ?? string.Empty,
                            "codex.exe");
                        if (File.Exists(siblingCodexPath))
                        {
                            return siblingCodexPath;
                        }
                    }
                    catch (Exception) when (process.HasExited)
                    {
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                    }
                }
            }
        }

        var installedPath = TryFindInstalledCodexExecutable();
        if (installedPath is not null)
        {
            return installedPath;
        }

        return "codex";
    }

    private static string? TryFindInstalledCodexExecutable()
    {
        var installDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAI",
            "Codex",
            "bin");

        try
        {
            return Directory.EnumerateFiles(installDirectory, "codex.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    public static bool TryFindDisplayableHostWindow(nint overlayWindowHandle, out HostBounds bounds)
    {
        HostBounds? firstVisibleHost = null;
        HostBounds? firstObscuredAnchorHost = null;

        for (var handle = GetTopWindow(nint.Zero);
             handle != nint.Zero;
             handle = GetWindow(handle, GwHwndNext))
        {
            if (!IsWindowVisible(handle) || IsIconic(handle))
            {
                continue;
            }

            GetWindowThreadProcessId(handle, out var processId);
            try
            {
                using var process = Process.GetProcessById((int)processId);
                if (!string.Equals(process.ProcessName, DesktopProcessName, StringComparison.OrdinalIgnoreCase) || !GetWindowRect(handle, out var rect))
                {
                    continue;
                }

                var candidate = new HostBounds(rect.Left, rect.Top, rect.Right, rect.Bottom);
                if (candidate.Area >= 120_000)
                {
                    TryFindCodexLayout(
                        handle,
                        candidate,
                        out var sidebarAnchorRectPixels,
                        out var hasModalDialog);
                    candidate = candidate with
                    {
                        WindowHandle = handle,
                        SidebarAnchorRectPixels = sidebarAnchorRectPixels,
                        HasModalDialog = hasModalDialog
                    };

                    if (candidate.HasModalDialog && IsHostWindowForeground(handle))
                    {
                        bounds = candidate;
                        return true;
                    }

                    if (sidebarAnchorRectPixels is not null)
                    {
                        if (!IsRectObscuredByWindowAbove(
                                handle, sidebarAnchorRectPixels.Value, overlayWindowHandle))
                        {
                            bounds = candidate;
                            return true;
                        }

                        firstObscuredAnchorHost ??= candidate;
                    }

                    firstVisibleHost ??= candidate;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        if (firstObscuredAnchorHost is { } obscuredHost)
        {
            bounds = obscuredHost;
            return true;
        }

        if (firstVisibleHost is { } visibleHost)
        {
            bounds = visibleHost;
            return true;
        }

        bounds = default;
        return false;
    }

    private static bool IsHostWindowForeground(nint hostWindowHandle)
    {
        if (hostWindowHandle == nint.Zero)
        {
            return false;
        }

        var foregroundWindowHandle = GetForegroundWindow();
        if (foregroundWindowHandle == nint.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(hostWindowHandle, out var hostProcessId);
        GetWindowThreadProcessId(foregroundWindowHandle, out var foregroundProcessId);
        return hostProcessId != 0 && hostProcessId == foregroundProcessId;
    }

    private static bool IsRectObscuredByWindowAbove(
        nint hostWindowHandle,
        Rect targetRect,
        nint overlayWindowHandle)
    {
        for (var handle = GetWindow(hostWindowHandle, GwHwndPrev);
             handle != nint.Zero;
             handle = GetWindow(handle, GwHwndPrev))
        {
            if (handle == overlayWindowHandle || !IsWindowVisible(handle) || IsIconic(handle) ||
                !GetWindowRect(handle, out var rect))
            {
                continue;
            }

            var windowRect = new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            if (targetRect.IntersectsWith(windowRect))
            {
                return true;
            }
        }

        return false;
    }

    private static void TryFindCodexLayout(
        nint hostHandle,
        HostBounds hostBounds,
        out Rect? sidebarAnchorRectPixels,
        out bool hasModalDialog)
    {
        sidebarAnchorRectPixels = null;
        hasModalDialog = false;

        try
        {
            var root = AutomationElement.FromHandle(hostHandle);
            var dialogs = root.FindAll(TreeScope.Descendants, new PropertyCondition(
                AutomationElement.ControlTypeProperty, ControlType.Window));
            foreach (AutomationElement dialog in dialogs)
            {
                var current = dialog.Current;
                if (!current.IsOffscreen && !current.BoundingRectangle.IsEmpty)
                {
                    hasModalDialog = true;
                    break;
                }
            }

            Rect? profileRectPixels = null;
            var sidebarButtons = root.FindAll(TreeScope.Descendants, new PropertyCondition(
                AutomationElement.ControlTypeProperty, ControlType.Button));
            foreach (AutomationElement button in sidebarButtons)
            {
                var current = button.Current;
                var rect = current.BoundingRectangle;
                if (!current.IsOffscreen &&
                    rect.Width is >= 20 and <= 80 && rect.Height is >= 20 and <= 80 &&
                    rect.Left >= hostBounds.Left && rect.Right <= hostBounds.Left + 120 &&
                    rect.Bottom >= hostBounds.Bottom - 200 && rect.Bottom <= hostBounds.Bottom)
                {
                    if (string.Equals(current.Name, "用量", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(current.Name, "Usage", StringComparison.OrdinalIgnoreCase))
                    {
                        sidebarAnchorRectPixels = rect;
                        break;
                    }

                    if (string.Equals(current.Name, "打开个人资料菜单", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(current.Name, "Open profile menu", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(current.Name, "Open account menu", StringComparison.OrdinalIgnoreCase))
                    {
                        profileRectPixels = rect;
                    }
                }
            }
            sidebarAnchorRectPixels ??= profileRectPixels;

        }
        catch (ElementNotAvailableException)
        {
        }
        catch (COMException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint windowHandle, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern nint GetTopWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint windowHandle, uint command);

    private const uint GwHwndNext = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

internal static class NativeWindowHelper
{
    private const int GwlExStyle = -20;
    private const uint EventSystemForeground = 0x0003;
    private const uint GwHwndPrev = 3;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private static readonly nint HwndTop = nint.Zero;

    public delegate void ForegroundChangedCallback(nint hook, uint eventType, nint windowHandle,
        int objectId, int childId, uint eventThread, uint eventTime);

    public static nint WatchForegroundChanges(ForegroundChangedCallback callback) =>
        SetWinEventHook(EventSystemForeground, EventSystemForeground, nint.Zero, callback, 0, 0, 0);

    public static void StopWatchingForegroundChanges(nint hook)
    {
        if (hook != nint.Zero)
        {
            UnhookWinEvent(hook);
        }
    }

    public static bool IsVisibleHost(nint handle) =>
        handle != nint.Zero && IsWindowVisible(handle) && !IsIconic(handle);

    public static nint GetForegroundWindowHandle() => GetForegroundWindow();

    public static nint GetWindowHandle(Window window)
    {
        return new WindowInteropHelper(window).Handle;
    }

    public static void EnableClickThrough(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var current = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(handle, GwlExStyle, new nint(current | WsExTransparent | WsExToolWindow | WsExNoActivate));
    }

    public static void KeepAboveHost(Window window, nint hostWindowHandle)
    {
        var handle = GetWindowHandle(window);
        if (handle == nint.Zero || hostWindowHandle == nint.Zero)
        {
            return;
        }

        var insertAfter = GetWindow(hostWindowHandle, GwHwndPrev);
        while (insertAfter == handle)
        {
            insertAfter = GetWindow(insertAfter, GwHwndPrev);
        }

        SetWindowPos(
            handle,
            insertAfter == nint.Zero ? HwndTop : insertAfter,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint windowHandle, int index, nint value);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module,
        ForegroundChangedCallback callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint handle);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint windowHandle, uint command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint windowHandle,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
