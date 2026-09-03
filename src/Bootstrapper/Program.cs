using System.Diagnostics;
using CodexQuota.RuntimeSupport;

namespace CodexQuota.Bootstrapper;

internal static class Program
{
    private static void Main(string[] args)
    {
        if (!RuntimeRequirement.IsDesktopRuntimeAvailable())
        {
            RuntimeRequirement.ShowMissingRuntimeMessage();
            return;
        }

        var runtimeDirectory = Path.Combine(AppContext.BaseDirectory, "runtime");
        var companionPath = Path.Combine(runtimeDirectory, "codex-quota.exe");
        if (!File.Exists(companionPath))
        {
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
        }
        catch
        {
        }
    }
}
