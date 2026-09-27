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
    public Rect? InputRectPixels { get; init; }
    public Rect? ComposerRectPixels { get; init; }
    public Rect? PlusRectPixels { get; init; }
    public bool IsInputObscured { get; init; }
    public bool HasImagePreviewOpen { get; init; }

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

    public static bool TryFindVisibleHostWindow(nint overlayWindowHandle, out HostBounds bounds)
    {
        HostBounds? firstVisibleHost = null;
        HostBounds? firstObscuredInputHost = null;

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
                        out var inputRectPixels,
                        out var composerRectPixels,
                        out var plusRectPixels,
                        out var hasImagePreviewOpen);
                    candidate = candidate with
                    {
                        WindowHandle = handle,
                        InputRectPixels = inputRectPixels,
                        ComposerRectPixels = composerRectPixels,
                        PlusRectPixels = plusRectPixels,
                        HasImagePreviewOpen = hasImagePreviewOpen
                    };

                    if (inputRectPixels is not null)
                    {
                        var visibleInputRect = composerRectPixels ?? inputRectPixels.Value;
                        candidate = candidate with
                        {
                            IsInputObscured = IsRectObscuredByWindowAbove(
                                handle,
                                visibleInputRect,
                                overlayWindowHandle)
                        };

                        if (!candidate.IsInputObscured)
                        {
                            bounds = candidate;
                            return true;
                        }

                        firstObscuredInputHost ??= candidate;
                    }

                    firstVisibleHost ??= candidate;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        if (firstObscuredInputHost is { } obscuredHost)
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

    public static bool TryFindDisplayableHostWindow(nint overlayWindowHandle, out HostBounds bounds)
    {
        if (!TryFindVisibleHostWindow(overlayWindowHandle, out bounds) ||
            (!bounds.HasImagePreviewOpen &&
             !IsHostWindowForeground(bounds.WindowHandle) &&
             (bounds.InputRectPixels is null || bounds.IsInputObscured)))
        {
            bounds = default;
            return false;
        }

        return true;
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
        out Rect? inputRectPixels,
        out Rect? composerRectPixels,
        out Rect? plusRectPixels,
        out bool hasImagePreviewOpen)
    {
        inputRectPixels = null;
        composerRectPixels = null;
        plusRectPixels = null;
        hasImagePreviewOpen = false;

        try
        {
            var root = AutomationElement.FromHandle(hostHandle);
            hasImagePreviewOpen = HasLargeCenteredImage(root, hostBounds);

            var editCondition = new PropertyCondition(
                AutomationElement.ControlTypeProperty,
                ControlType.Edit);
            var editCandidates = ReadAutomationSnapshots(root.FindAll(TreeScope.Descendants, editCondition));
            var edit = editCandidates
                .Where(candidate =>
                    candidate.Rect.Width >= 240 &&
                    candidate.Rect.Left >= hostBounds.Left &&
                    candidate.Rect.Right <= hostBounds.Right + 40 &&
                    candidate.Rect.Bottom >= hostBounds.Bottom - 300 &&
                    candidate.Rect.Bottom <= hostBounds.Bottom + 20)
                .OrderByDescending(candidate => candidate.ClassName.Contains("ProseMirror", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(candidate => candidate.Rect.Width)
                .ThenByDescending(candidate => candidate.Rect.Bottom)
                .FirstOrDefault();

            if (edit.Element is null)
            {
                return;
            }

            inputRectPixels = edit.Rect;

            if (TryFindComposerContainer(
                    edit.Element,
                    edit.Rect,
                    hostBounds,
                    out var composerRect))
            {
                composerRectPixels = composerRect;
            }

            var buttonCondition = new PropertyCondition(
                AutomationElement.ControlTypeProperty,
                ControlType.Button);
            var buttonCandidates = ReadAutomationSnapshots(root.FindAll(TreeScope.Descendants, buttonCondition));
            var bottomReference = composerRectPixels?.Bottom ?? hostBounds.Bottom;
            var leftReference = composerRectPixels?.Left ?? inputRectPixels.Value.Left;
            var plus = buttonCandidates
                .Where(candidate =>
                    candidate.Rect.Width is >= 20 and <= 48 &&
                    candidate.Rect.Height is >= 20 and <= 48 &&
                    candidate.Rect.Left >= hostBounds.Left &&
                    candidate.Rect.Right <= hostBounds.Right + 20 &&
                    candidate.Rect.Left >= leftReference - 24 &&
                    candidate.Rect.Left <= leftReference + 160 &&
                    candidate.Rect.Bottom >= bottomReference - 100 &&
                    candidate.Rect.Bottom <= hostBounds.Bottom + 20)
                .OrderByDescending(candidate => candidate.ClassName.Contains("aspect-square", StringComparison.OrdinalIgnoreCase))
                .ThenBy(candidate => Math.Abs(candidate.Rect.Left - leftReference))
                .ThenByDescending(candidate => candidate.Rect.Bottom)
                .FirstOrDefault();

            if (plus.Element is not null)
            {
                plusRectPixels = plus.Rect;
            }
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

    private static bool HasLargeCenteredImage(AutomationElement root, HostBounds hostBounds)
    {
        var imageCondition = new PropertyCondition(
            AutomationElement.ControlTypeProperty,
            ControlType.Image);
        var images = ReadAutomationSnapshots(root.FindAll(TreeScope.Descendants, imageCondition));
        var hostWidth = hostBounds.Right - hostBounds.Left;
        var hostHeight = hostBounds.Bottom - hostBounds.Top;

        return images.Any(image =>
            image.Rect.Width >= Math.Max(280, hostWidth * 0.35) &&
            image.Rect.Height >= Math.Max(150, hostHeight * 0.35) &&
            image.Rect.Width * image.Rect.Height >= hostWidth * hostHeight * 0.12 &&
            Math.Abs(image.Rect.Left + image.Rect.Width / 2 - (hostBounds.Left + hostWidth / 2)) <= hostWidth * 0.25 &&
            Math.Abs(image.Rect.Top + image.Rect.Height / 2 - (hostBounds.Top + hostHeight / 2)) <= hostHeight * 0.25);
    }

    private static List<AutomationSnapshot> ReadAutomationSnapshots(AutomationElementCollection elements)
    {
        var snapshots = new List<AutomationSnapshot>(elements.Count);
        foreach (AutomationElement element in elements)
        {
            try
            {
                var current = element.Current;
                var rect = current.BoundingRectangle;
                if (!current.IsOffscreen && rect.Width > 0 && rect.Height > 0)
                {
                    snapshots.Add(new AutomationSnapshot(
                        element,
                        rect,
                        current.ClassName));
                }
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

        return snapshots;
    }

    private static bool TryFindComposerContainer(
        AutomationElement edit,
        Rect editRect,
        HostBounds hostBounds,
        out Rect composerRect)
    {
        var walker = TreeWalker.RawViewWalker;
        var current = walker.GetParent(edit);
        for (var level = 0; level < 8 && current is not null; level++)
        {
            try
            {
                var snapshot = current.Current;
                var rect = snapshot.BoundingRectangle;
                if (!snapshot.IsOffscreen &&
                    rect.Width > editRect.Width + 16 &&
                    rect.Width <= editRect.Width + 240 &&
                    rect.Height > editRect.Height + 30 &&
                    rect.Height <= 260 &&
                    rect.Left <= editRect.Left &&
                    rect.Right >= editRect.Right &&
                    rect.Bottom >= hostBounds.Bottom - 300 &&
                    rect.Bottom <= hostBounds.Bottom + 20)
                {
                    composerRect = rect;
                    return true;
                }

                current = walker.GetParent(current);
            }
            catch (ElementNotAvailableException)
            {
                break;
            }
            catch (COMException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                break;
            }
        }

        composerRect = default;
        return false;
    }

    private readonly record struct AutomationSnapshot(
        AutomationElement? Element,
        Rect Rect,
        string ClassName);

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
    private const uint GwHwndPrev = 3;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private static readonly nint HwndTop = nint.Zero;

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
