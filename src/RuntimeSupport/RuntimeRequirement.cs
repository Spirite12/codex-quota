using System.Runtime.InteropServices;

namespace CodexQuota.RuntimeSupport;

internal static class RuntimeRequirement
{
    private const uint MessageBoxOk = 0x00000000;
    private const uint MessageBoxIconInformation = 0x00000040;

    public static bool IsDesktopRuntimeAvailable()
    {
        var roots = new[]
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "dotnet")
        };

        foreach (var root in roots.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var desktopRuntimePath = Path.Combine(root!, "shared", "Microsoft.WindowsDesktop.App");
            try
            {
                if (Directory.Exists(desktopRuntimePath) &&
                    Directory.EnumerateDirectories(desktopRuntimePath, "10.*", SearchOption.TopDirectoryOnly).Any())
                {
                    return true;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return false;
    }

    public static void ShowMissingRuntimeMessage()
    {
        MessageBox(
            nint.Zero,
            "Please install Microsoft .NET 10 Desktop Runtime and try again.",
            "Codex Quota - Runtime Required",
            MessageBoxOk | MessageBoxIconInformation);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(nint hWnd, string text, string caption, uint type);
}
