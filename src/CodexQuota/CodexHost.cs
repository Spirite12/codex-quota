using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;

namespace CodexQuota;

internal readonly record struct HostBounds(int Left, int Top, int Right, int Bottom)
{
    public int? ApprovalRight { get; init; }
    public int? ComposerBottomPixels { get; init; }
    public int? PlusCenterYPixels { get; init; }

    public long Area => (long)Math.Max(0, Right - Left) * Math.Max(0, Bottom - Top);
}

internal static class CodexHost
{
    private const string DesktopProcessName = "ChatGPT";
    private const string CodexProcessName = "codex";
    private const string CodexHostProcessName = "codex-code-mode-host";

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

    public static bool TryFindVisibleHostWindow(out HostBounds bounds)
    {
        var candidates = new List<(nint Handle, uint ProcessId, HostBounds Bounds)>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || IsIconic(handle))
            {
                return true;
            }

            GetWindowThreadProcessId(handle, out var processId);
            try
            {
                using var process = Process.GetProcessById((int)processId);
                if (!string.Equals(process.ProcessName, DesktopProcessName, StringComparison.OrdinalIgnoreCase) || !GetWindowRect(handle, out var rect))
                {
                    return true;
                }

                var candidate = new HostBounds(rect.Left, rect.Top, rect.Right, rect.Bottom);
                if (candidate.Area >= 120_000)
                {
                candidates.Add((handle, processId, candidate));
                }
            }
            catch (ArgumentException)
            {
            }

            return true;
        }, nint.Zero);

        var foreground = GetForegroundWindow();
        if (foreground == nint.Zero)
        {
            bounds = default;
            return false;
        }

        GetWindowThreadProcessId(foreground, out var foregroundProcessId);
        var activeCandidates = candidates
            .Where(candidate => candidate.Handle == foreground || candidate.ProcessId == foregroundProcessId)
            .ToList();

        if (activeCandidates.Count > 0)
        {
            var activeCandidate = activeCandidates.OrderByDescending(candidate => candidate.Bounds.Area).First();
            bounds = activeCandidate.Bounds;
            TryFindCodexLayout(
                activeCandidate.Handle,
                activeCandidate.Bounds,
                out var approvalRight,
                out var composerBottomPixels,
                out var plusCenterYPixels);
            bounds = bounds with
            {
                ApprovalRight = approvalRight,
                ComposerBottomPixels = composerBottomPixels,
                PlusCenterYPixels = plusCenterYPixels
            };

            return true;
        }

        bounds = default;
        return false;
    }

    private static void TryFindCodexLayout(
        nint hostHandle,
        HostBounds hostBounds,
        out int? approvalRight,
        out int? composerBottomPixels,
        out int? plusCenterYPixels)
    {
        approvalRight = null;
        composerBottomPixels = null;
        plusCenterYPixels = null;

        try
        {
            var root = AutomationElement.FromHandle(hostHandle);

            var textCondition = new PropertyCondition(
                AutomationElement.ControlTypeProperty,
                ControlType.Text);
            var approval = root.FindAll(TreeScope.Descendants, textCondition)
                .Cast<AutomationElement>()
                .Select(element => new
                {
                    Rect = element.Current.BoundingRectangle,
                    Name = element.Current.Name,
                    IsOffscreen = element.Current.IsOffscreen
                })
                .Where(candidate =>
                    !candidate.IsOffscreen &&
                    candidate.Rect.Width > 0 &&
                    candidate.Rect.Height > 0 &&
                    string.Equals(candidate.Name, "帮我批准", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(candidate => candidate.Rect.Bottom)
                .FirstOrDefault();

            if (approval is not null)
            {
                approvalRight = (int)Math.Round(approval.Rect.Right);
            }

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

            if (edit.Element is not null && TryFindComposerContainer(
                    edit.Element,
                    edit.Rect,
                    hostBounds,
                    out var composerRect))
            {
                composerBottomPixels = (int)Math.Round(composerRect.Bottom);
            }

            var buttonCondition = new PropertyCondition(
                AutomationElement.ControlTypeProperty,
                ControlType.Button);
            var buttonCandidates = ReadAutomationSnapshots(root.FindAll(TreeScope.Descendants, buttonCondition));
            var bottomReference = composerBottomPixels ?? hostBounds.Bottom;
            var plus = buttonCandidates
                .Where(candidate =>
                    candidate.Rect.Width is >= 20 and <= 48 &&
                    candidate.Rect.Height is >= 20 and <= 48 &&
                    candidate.Rect.Left >= hostBounds.Left &&
                    candidate.Rect.Right <= hostBounds.Right + 20 &&
                    candidate.Rect.Bottom >= bottomReference - 100 &&
                    candidate.Rect.Bottom <= hostBounds.Bottom + 20)
                .OrderByDescending(candidate => IsAddFilesButton(candidate.Name))
                .ThenBy(candidate => candidate.Rect.Left)
                .ThenByDescending(candidate => candidate.Rect.Bottom)
                .FirstOrDefault();

            if (plus.Element is not null)
            {
                plusCenterYPixels = (int)Math.Round((plus.Rect.Top + plus.Rect.Bottom) / 2);
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
                        current.Name,
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

    private static bool IsAddFilesButton(string name)
    {
        return name.Contains("添加文件", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("add files", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("attach", StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct AutomationSnapshot(
        AutomationElement? Element,
        Rect Rect,
        string Name,
        string ClassName);

    private delegate bool EnumWindowsProc(nint windowHandle, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint windowHandle, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

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
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;

    public static void EnableClickThrough(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var current = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(handle, GwlExStyle, new nint(current | WsExTransparent | WsExToolWindow | WsExNoActivate));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint windowHandle, int index, nint value);
}
