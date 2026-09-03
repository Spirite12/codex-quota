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
                out var approvalRight);
            bounds = bounds with
            {
                ApprovalRight = approvalRight
            };

            return true;
        }

        bounds = default;
        return false;
    }

    private static void TryFindCodexLayout(
        nint hostHandle,
        out int? approvalRight)
    {
        approvalRight = null;

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
