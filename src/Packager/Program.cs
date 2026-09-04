using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using CodexQuota.Localization;

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
                return Fail(UiText.T(
                    "无法定位 Codex Quota 项目源码目录。",
                    "Unable to locate the codex-quota project root."));

            var installerPath = PromptForInstallerPath(projectRoot.FullName);
            if (installerPath is null)
                return 0;

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
                return Fail(UiText.T(
                    "未生成安装器启动文件。",
                    "The setup stub was not produced."));

            var installerDirectory = Path.GetDirectoryName(installerPath);
            if (string.IsNullOrWhiteSpace(installerDirectory))
                return Fail(UiText.T(
                    "无法确定安装包输出目录。",
                    "Unable to determine the installer output directory."));

            Directory.CreateDirectory(installerDirectory);
            CreateSelfExtractingInstaller(setupStub, payloadZip, installerPath);

            Directory.Delete(stagingRoot, recursive: true);
            Console.WriteLine();
            Console.WriteLine(UiText.T(
                $"安装包已生成：{installerPath}",
                $"Installer created: {installerPath}"));
            Console.WriteLine(UiText.T(
                "安装包已准备好，尚未执行安装。",
                "The installer is ready. It has not been executed."));
            WaitForExit();
            return 0;
        }
        catch (Exception exception)
        {
            return Fail(UiText.T(
                $"打包失败。\n\n{exception.Message}",
                $"Packaging failed.\n\n{exception.Message}"));
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

    private static string? PromptForInstallerPath(string projectRoot)
    {
        var defaultPath = Path.Combine(projectRoot, "codex-quota.exe");

        Console.WriteLine(UiText.T(
            "请输入安装包生成位置。可以输入文件夹，或输入完整的 .exe 文件路径。",
            "Enter the installer output location. You can enter a folder or a full .exe file path."));
        Console.WriteLine(UiText.T(
            $"直接回车使用默认路径：{defaultPath}",
            $"Press Enter to use the default path: {defaultPath}"));
        Console.WriteLine(UiText.T(
            "如果使用 C 盘根目录，请输入：C:\\",
            "For the C drive root, enter: C:\\"));
        Console.Write("> ");

        var input = Console.ReadLine();
        if (input is null)
            return null;

        input = input.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(input))
            return defaultPath;

        var selectedPath = Path.GetFullPath(input);
        return string.Equals(Path.GetExtension(selectedPath), ".exe", StringComparison.OrdinalIgnoreCase)
            ? selectedPath
            : Path.Combine(selectedPath, "codex-quota.exe");
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
            ?? throw new InvalidOperationException(
                UiText.T("无法启动 dotnet。", "Unable to start dotnet."));
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                UiText.T(
                    $"dotnet {arguments[0]} 执行失败，退出代码为 {process.ExitCode}。",
                    $"dotnet {arguments[0]} failed with exit code {process.ExitCode}."));
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
                UiText.T(
                    "临时打包目录位于项目构建目录之外。",
                    "The staging path is outside the project build directory."));
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
        Console.WriteLine(UiText.T("按任意键关闭。", "Press any key to close."));
        try
        {
            Console.ReadKey(intercept: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
