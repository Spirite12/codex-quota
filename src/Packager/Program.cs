using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace CodexQuota.Packager;

internal static class Program
{
    private const string PayloadMagicText = "CODEXQUOTA_PAYLOAD_V1";
    private static readonly byte[] PayloadMagic = Encoding.ASCII.GetBytes(PayloadMagicText);

    private static int Main()
    {
        try
        {
            var projectRoot = FindProjectRoot();
            if (projectRoot is null)
                return Fail("Unable to locate the codex-quota project root.");

            var buildRoot = Path.Combine(projectRoot.FullName, "build");
            var stagingRoot = Path.Combine(buildRoot, "package");
            EnsureSafeStagingPath(projectRoot.FullName, stagingRoot);
            if (Directory.Exists(stagingRoot))
                Directory.Delete(stagingRoot, recursive: true);

            Directory.CreateDirectory(stagingRoot);
            var publishRoot = Path.Combine(stagingRoot, "publish");
            var mainOutput = Path.Combine(publishRoot, "main");
            var bootstrapperOutput = Path.Combine(publishRoot, "bootstrapper");
            var launcherOutput = Path.Combine(publishRoot, "launcher");
            var uninstallerOutput = Path.Combine(publishRoot, "uninstaller");
            var setupOutput = Path.Combine(publishRoot, "setup");
            var payloadRoot = Path.Combine(stagingRoot, "payload");
            Directory.CreateDirectory(payloadRoot);

            var projects = new[]
            {
                Path.Combine(projectRoot.FullName, "src", "CodexQuota", "CodexQuota.csproj"),
                Path.Combine(projectRoot.FullName, "src", "Bootstrapper", "Bootstrapper.csproj"),
                Path.Combine(projectRoot.FullName, "src", "Launcher", "Launcher.csproj"),
                Path.Combine(projectRoot.FullName, "src", "Uninstaller", "Uninstaller.csproj"),
                Path.Combine(projectRoot.FullName, "src", "Installer", "Installer.csproj")
            };

            foreach (var project in projects)
            {
                RunDotnet(
                    projectRoot.FullName,
                    "restore",
                    project,
                    "--runtime", "win-x64",
                    "--ignore-failed-sources");
            }

            PublishFrameworkDependentMain(projectRoot.FullName, projects[0], mainOutput);
            PublishFrameworkDependentSingleFile(projectRoot.FullName, projects[1], bootstrapperOutput);
            PublishFrameworkDependentSingleFile(projectRoot.FullName, projects[2], launcherOutput);
            PublishFrameworkDependentSingleFile(projectRoot.FullName, projects[3], uninstallerOutput);
            PublishFrameworkDependentSingleFile(projectRoot.FullName, projects[4], setupOutput);

            var runtimePayloadRoot = Path.Combine(payloadRoot, "runtime");
            CopyDirectoryContents(mainOutput, runtimePayloadRoot);
            CopyDirectoryContents(bootstrapperOutput, payloadRoot);
            CopyDirectoryContents(launcherOutput, payloadRoot);
            CopyDirectoryContents(uninstallerOutput, payloadRoot);
            var readmePath = Path.Combine(projectRoot.FullName, "README.md");
            if (File.Exists(readmePath))
                File.Copy(readmePath, Path.Combine(payloadRoot, "README.md"), overwrite: true);

            var payloadZip = Path.Combine(stagingRoot, "codex-quota.payload.zip");
            ZipFile.CreateFromDirectory(
                payloadRoot,
                payloadZip,
                CompressionLevel.Optimal,
                includeBaseDirectory: false);

            var setupStub = Path.Combine(setupOutput, "codex-quota-setup.exe");
            if (!File.Exists(setupStub))
                return Fail("The setup stub was not produced.");

            var driveRoot = Path.GetPathRoot(projectRoot.FullName);
            if (string.IsNullOrWhiteSpace(driveRoot))
                return Fail("Unable to determine the project drive root.");

            var installerPath = Path.Combine(driveRoot, "codex-quota.exe");
            CreateSelfExtractingInstaller(setupStub, payloadZip, installerPath);

            Directory.Delete(stagingRoot, recursive: true);
            Console.WriteLine();
            Console.WriteLine($"Installer created: {installerPath}");
            Console.WriteLine("The installer is ready. It has not been executed.");
            WaitForExit();
            return 0;
        }
        catch (Exception exception)
        {
            return Fail($"Packaging failed.\n\n{exception.Message}");
        }
    }

    private static DirectoryInfo? FindProjectRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "src", "CodexQuota", "CodexQuota.csproj")))
                return current;
            current = current.Parent;
        }

        return null;
    }

    private static void PublishFrameworkDependentMain(string workingDirectory, string projectPath, string outputPath)
    {
        RunDotnet(
            workingDirectory,
            "publish",
            projectPath,
            "--no-restore",
            "--configuration", "Release",
            "--runtime", "win-x64",
            "--self-contained", "false",
            "-p:PublishSingleFile=false",
            "-p:PublishReadyToRun=false",
            "-p:DebugType=None",
            "-p:DebugSymbols=false",
            "--output", outputPath);
    }

    private static void PublishFrameworkDependentSingleFile(string workingDirectory, string projectPath, string outputPath)
    {
        RunDotnet(
            workingDirectory,
            "publish",
            projectPath,
            "--no-restore",
            "--configuration", "Release",
            "--runtime", "win-x64",
            "--self-contained", "false",
            "-p:PublishSingleFile=true",
            "-p:IncludeNativeLibrariesForSelfExtract=true",
            "-p:DebugType=None",
            "-p:DebugSymbols=false",
            "--output", outputPath);
    }

    private static void RunDotnet(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = false
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start dotnet.");
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"dotnet {arguments[0]} failed with exit code {process.ExitCode}.");
    }

    private static void CopyDirectoryContents(string sourceDirectory, string destinationDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException(sourceDirectory);

        Directory.CreateDirectory(destinationDirectory);
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relativePath));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(file, destinationPath, overwrite: true);
        }
    }

    private static void CreateSelfExtractingInstaller(string setupStub, string payloadZip, string outputPath)
    {
        File.Copy(setupStub, outputPath, overwrite: true);

        using var output = new FileStream(outputPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var payload = new FileStream(payloadZip, FileMode.Open, FileAccess.Read, FileShare.Read);
        payload.CopyTo(output);
        output.Write(BitConverter.GetBytes(payload.Length));
        output.Write(PayloadMagic);
    }

    private static void EnsureSafeStagingPath(string projectRoot, string stagingRoot)
    {
        var fullRoot = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullStaging = Path.GetFullPath(stagingRoot).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!fullStaging.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
            || !fullStaging.EndsWith("build\\package\\", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The staging path is outside the project build directory.");
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        WaitForExit();
        return 1;
    }

    private static void WaitForExit()
    {
        if (!Environment.UserInteractive)
            return;

        Console.WriteLine();
        Console.WriteLine("Press any key to close.");
        try
        {
            Console.ReadKey(intercept: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
