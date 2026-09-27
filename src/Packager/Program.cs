using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using CodexQuota.Localization;

namespace CodexQuota.Packager;

internal static class Program
{
    private const string PayloadMagicText = "CODEXQUOTA_PAYLOAD_V1";
    private const string DesktopRuntimeVersion = "10.0.11";
    private const string DesktopRuntimeInstallerUrl =
        "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.11/windowsdesktop-runtime-10.0.11-win-x64.exe";
    private const string DesktopRuntimeInstallerSha512 =
        "4dbf26b0b78f55c5f59a46c3c81327b23a04f449f7ac6798204dcd19d99459258936daaede61d1b8c1ba523d6c26bf68bac86b3371d22e67cef235edbdc2f26c";
    private const string DesktopRuntimeInstallerFileName =
        "windowsdesktop-runtime-" + DesktopRuntimeVersion + "-win-x64.exe";
    private const string InnerSetupFileName = "codex-quota-setup-inner.exe";
    private const string RuntimeBootstrapScriptFileName = "install-codex-quota.ps1";
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
            var outerSourceRoot = Path.Combine(stagingRoot, "outer-source");
            Directory.CreateDirectory(payloadRoot);
            Directory.CreateDirectory(outerSourceRoot);

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

            DownloadDesktopRuntime(
                projectRoot.FullName,
                Path.Combine(outerSourceRoot, DesktopRuntimeInstallerFileName));

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

            var innerSetupPath = Path.Combine(outerSourceRoot, InnerSetupFileName);
            CreateSelfExtractingInstaller(setupStub, payloadZip, innerSetupPath);
            CreateRuntimeBootstrapScript(outerSourceRoot);

            var installerDirectory = Path.GetDirectoryName(installerPath);
            if (string.IsNullOrWhiteSpace(installerDirectory))
                return Fail(UiText.T(
                    "无法确定安装包输出目录。",
                    "Unable to determine the installer output directory."));

            Directory.CreateDirectory(installerDirectory);
            CreateIExpressInstaller(outerSourceRoot, installerPath);

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
        var defaultPath = Path.Combine(projectRoot, "codex-quota-setup.exe");

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
            : Path.Combine(selectedPath, "codex-quota-setup.exe");
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

    private static void DownloadDesktopRuntime(string projectRoot, string destinationPath)
    {
        var cacheDirectory = Path.Combine(projectRoot, "build", "dependencies");
        var cachedPath = Path.Combine(cacheDirectory, DesktopRuntimeInstallerFileName);
        Directory.CreateDirectory(cacheDirectory);

        if (File.Exists(cachedPath))
        {
            try
            {
                VerifyDesktopRuntime(cachedPath);
                File.Copy(cachedPath, destinationPath, overwrite: true);
                Console.WriteLine(UiText.T(
                    "已使用缓存的 Microsoft .NET 10 Desktop Runtime 安装程序。",
                    "Using the cached Microsoft .NET 10 Desktop Runtime installer."));
                return;
            }
            catch (InvalidDataException)
            {
                File.Delete(cachedPath);
            }
        }

        var temporaryPath = Path.Combine(
            cacheDirectory,
            $"{DesktopRuntimeInstallerFileName}.{Guid.NewGuid():N}.download");
        try
        {
            Console.WriteLine(UiText.T(
                "正在下载 Microsoft .NET 10 Desktop Runtime 安装程序。",
                "Downloading the Microsoft .NET 10 Desktop Runtime installer."));

            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(10)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("codex-quota-build/0.2.0");
            using var response = client.GetAsync(
                    DesktopRuntimeInstallerUrl,
                    HttpCompletionOption.ResponseHeadersRead)
                .GetAwaiter()
                .GetResult();
            response.EnsureSuccessStatusCode();

            using (var input = response.Content.ReadAsStream())
            using (var output = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }

            VerifyDesktopRuntime(temporaryPath);
            File.Move(temporaryPath, cachedPath, overwrite: true);
            File.Copy(cachedPath, destinationPath, overwrite: true);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                UiText.T(
                    $"无法下载或校验 Microsoft .NET 10 Desktop Runtime 安装程序。\n\n{exception.Message}",
                    $"Unable to download or verify the Microsoft .NET 10 Desktop Runtime installer.\n\n{exception.Message}"),
                exception);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
            }
        }
    }

    private static void VerifyDesktopRuntime(string installerPath)
    {
        using var input = File.OpenRead(installerPath);
        var actualHash = Convert.ToHexString(SHA512.HashData(input));
        if (!string.Equals(actualHash, DesktopRuntimeInstallerSha512, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                UiText.T(
                    "Microsoft .NET 10 Desktop Runtime 安装程序校验失败。",
                    "The Microsoft .NET 10 Desktop Runtime installer failed hash verification."));
        }
    }

    private static void CreateRuntimeBootstrapScript(string sourceRoot)
    {
        var scriptPath = Path.Combine(sourceRoot, RuntimeBootstrapScriptFileName);
        const string scriptTemplate = """
            #requires -Version 5.1
            $ErrorActionPreference = 'Stop'

            function Test-DesktopRuntime {
                $roots = @(
                    $env:DOTNET_ROOT_X64
                    $env:DOTNET_ROOT
                    (Join-Path $env:ProgramFiles 'dotnet')
                ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique

                foreach ($root in $roots) {
                    $desktopRuntimePath = Join-Path $root 'shared\Microsoft.WindowsDesktop.App'
                    if (Test-Path -LiteralPath $desktopRuntimePath) {
                        $versions = @(Get-ChildItem -LiteralPath $desktopRuntimePath -Directory -Filter '10.*' -ErrorAction SilentlyContinue)
                        if ($versions.Count -gt 0) {
                            return $true
                        }
                    }
                }

                return $false
            }

            function Show-InstallerError {
                param([string] $Message)

                try {
                    Add-Type -AssemblyName System.Windows.Forms
                    [System.Windows.Forms.MessageBox]::Show(
                        $Message,
                        'Codex Quota',
                        [System.Windows.Forms.MessageBoxButtons]::OK,
                        [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
                }
                catch {
                    Write-Error $Message
                }
            }

            $isChinese = [System.Globalization.CultureInfo]::CurrentUICulture.TwoLetterISOLanguageName -eq 'zh'
            if (-not (Test-DesktopRuntime)) {
                try {
                    Start-Process `
                        -FilePath (Join-Path $PSScriptRoot '__RUNTIME_INSTALLER__') `
                        -WorkingDirectory $PSScriptRoot `
                        -Verb RunAs `
                        -Wait | Out-Null
                }
                catch {
                    $message = if ($isChinese) {
                        '无法启动 Microsoft .NET 10 Desktop Runtime 安装程序。'
                    }
                    else {
                        'Unable to start the Microsoft .NET 10 Desktop Runtime installer.'
                    }
                    Show-InstallerError $message
                    exit 1
                }

                if (-not (Test-DesktopRuntime)) {
                    $message = if ($isChinese) {
                        'Microsoft .NET 10 Desktop Runtime 安装未完成，Codex Quota 未安装。'
                    }
                    else {
                        'Microsoft .NET 10 Desktop Runtime was not installed. codex-quota was not installed.'
                    }
                    Show-InstallerError $message
                    exit 1
                }
            }

            try {
                $installerProcess = Start-Process `
                    -FilePath (Join-Path $PSScriptRoot '__INNER_SETUP__') `
                    -WorkingDirectory $PSScriptRoot `
                    -Wait `
                    -PassThru
                exit $installerProcess.ExitCode
            }
            catch {
                $message = if ($isChinese) {
                    '无法启动 Codex Quota 安装器。'
                }
                else {
                    'Unable to start the codex-quota installer.'
                }
                Show-InstallerError $message
                exit 1
            }
            """;

        var script = scriptTemplate
            .Replace("__RUNTIME_INSTALLER__", DesktopRuntimeInstallerFileName, StringComparison.Ordinal)
            .Replace("__INNER_SETUP__", InnerSetupFileName, StringComparison.Ordinal);
        File.WriteAllText(
            scriptPath,
            script,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static void CreateIExpressInstaller(string sourceRoot, string outputPath)
    {
        var packageId = Guid.NewGuid().ToString("N");
        var sedPath = Path.Combine(sourceRoot, $"codex-quota-setup-{packageId}.sed");
        var packageFileName = $"codex-quota-setup-{packageId}.exe";
        var packagePath = Path.Combine(sourceRoot, packageFileName);
        var pendingPath = Path.Combine(
            Path.GetDirectoryName(outputPath)!,
            $".codex-quota-setup-{packageId}.tmp");
        try
        {
            File.WriteAllText(
                sedPath,
                BuildIExpressSed(packageFileName),
                Encoding.ASCII);

            var iexpressPath = Path.Combine(Environment.SystemDirectory, "iexpress.exe");
            if (!File.Exists(iexpressPath))
            {
                throw new InvalidOperationException(
                    UiText.T(
                        "当前 Windows 中未找到 IExpress，无法生成小体积安装包。",
                        "IExpress was not found on this Windows installation, so the compact installer cannot be created."));
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = iexpressPath,
                WorkingDirectory = sourceRoot,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("/N");
            startInfo.ArgumentList.Add("/Q");
            startInfo.ArgumentList.Add(Path.GetFileName(sedPath));

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    UiText.T("无法启动 IExpress。", "Unable to start IExpress."));
            process.WaitForExit();

            if (process.ExitCode != 0 || !File.Exists(packagePath) || new FileInfo(packagePath).Length == 0)
            {
                throw new InvalidOperationException(
                    UiText.T(
                        $"IExpress 生成安装包失败，退出代码为 {process.ExitCode}。",
                        $"IExpress failed to create the installer with exit code {process.ExitCode}."));
            }

            // Copy beside the destination first, so a failed build or copy preserves the old installer.
            File.Copy(packagePath, pendingPath);
            if (File.Exists(outputPath))
                File.Replace(pendingPath, outputPath, destinationBackupFileName: null);
            else
                File.Move(pendingPath, outputPath);
        }
        finally
        {
            try
            {
                if (File.Exists(sedPath))
                    File.Delete(sedPath);
                if (File.Exists(packagePath))
                    File.Delete(packagePath);
                if (File.Exists(pendingPath))
                    File.Delete(pendingPath);
            }
            catch
            {
            }
        }
    }

    private static string BuildIExpressSed(string packageFileName)
    {
        return $"""
            [Version]
            Class=IEXPRESS
            SEDVersion=3

            [Options]
            PackagePurpose=InstallApp
            ShowInstallProgramWindow=0
            HideExtractAnimation=1
            UseLongFileName=1
            InsideCompressed=1
            CAB_FixedSize=0
            CAB_ResvCodeSigning=0
            RebootMode=N
            InstallPrompt=%InstallPrompt%
            DisplayLicense=%DisplayLicense%
            FinishMessage=%FinishMessage%
            TargetName=%TargetName%
            FriendlyName=%FriendlyName%
            AppLaunched=%AppLaunched%
            PostInstallCmd=%PostInstallCmd%
            AdminQuietInstCmd=%AdminQuietInstCmd%
            UserQuietInstCmd=%UserQuietInstCmd%
            SourceFiles=SourceFiles

            [Strings]
            InstallPrompt=
            DisplayLicense=
            FinishMessage=
            TargetName={packageFileName}
            FriendlyName=Codex Quota
            AppLaunched=cmd.exe /d /c powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File {RuntimeBootstrapScriptFileName}
            PostInstallCmd=<None>
            AdminQuietInstCmd=
            UserQuietInstCmd=
            FILE0="{InnerSetupFileName}"
            FILE1="{DesktopRuntimeInstallerFileName}"
            FILE2="{RuntimeBootstrapScriptFileName}"

            [SourceFiles]
            SourceFiles0=.\

            [SourceFiles0]
            %FILE0%=
            %FILE1%=
            %FILE2%=
            """;
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
