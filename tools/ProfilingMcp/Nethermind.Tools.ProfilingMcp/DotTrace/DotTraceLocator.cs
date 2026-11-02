// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Nethermind.Tools.ProfilingMcp.DotTrace;

/// <summary>
/// Finds the dotTrace command-line tools, downloading the free <c>JetBrains.dotTrace.CommandLineTools</c> NuGet package on first use.
/// </summary>
/// <remarks>
/// Set <c>DOTTRACE_TOOLS_DIR</c> to a directory containing <c>dottrace.exe</c> (Windows) or <c>dottrace</c> (Linux/macOS) to use an existing installation.
/// The <c>JetBrains.dotTrace.GlobalTools</c> package is not used because it lacks <c>Reporter.exe</c>.
/// </remarks>
public sealed class DotTraceLocator(WorkspacePaths paths, ILogger<DotTraceLocator> logger)
{
    internal const string Version = "2026.2.3.1";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _toolsDir;

    public static bool IsReporterSupported => OperatingSystem.IsWindows();

    public async Task<string> GetProfilerPathAsync(CancellationToken cancellationToken) =>
        Path.Combine(await GetToolsDirAsync(cancellationToken), OperatingSystem.IsWindows() ? "dottrace.exe" : "dottrace");

    public async Task<string> GetReporterPathAsync(CancellationToken cancellationToken)
    {
        if (!IsReporterSupported)
            throw new PlatformNotSupportedException("dotTrace Reporter ships for Windows only. Copy the snapshot files (*.dtp, *.dtp.*) to a Windows machine and analyze them there.");

        return Path.Combine(await GetToolsDirAsync(cancellationToken), "Reporter.exe");
    }

    private async Task<string> GetToolsDirAsync(CancellationToken cancellationToken)
    {
        if (_toolsDir is not null)
            return _toolsDir;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            _toolsDir ??= Environment.GetEnvironmentVariable("DOTTRACE_TOOLS_DIR") is { Length: > 0 } configured
                ? configured
                : await EnsureDownloadedAsync(cancellationToken);
            return _toolsDir;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<string> EnsureDownloadedAsync(CancellationToken cancellationToken)
    {
        string packageId = $"JetBrains.dotTrace.CommandLineTools.{GetRuntimeSuffix()}";
        string installDir = Path.Combine(paths.Tools, $"dottrace-{Version}");
        string toolsDir = Path.Combine(installDir, "tools");
        if (Directory.Exists(toolsDir))
            return toolsDir;

        string lowerId = packageId.ToLowerInvariant();
        Uri url = new($"https://api.nuget.org/v3-flatcontainer/{lowerId}/{Version}/{lowerId}.{Version}.nupkg");
        logger.LogInformation("Downloading {Url}", url);

        string tempDir = installDir + ".partial";
        if (Directory.Exists(tempDir))
            Directory.Delete(tempDir, recursive: true);

        // Zip extraction needs a seekable stream, so download to disk first.
        string packageFile = installDir + ".nupkg";
        using (HttpClient http = new())
        await using (Stream download = await http.GetStreamAsync(url, cancellationToken))
        await using (FileStream file = File.Create(packageFile))
            await download.CopyToAsync(file, cancellationToken);

        await ZipFile.ExtractToDirectoryAsync(packageFile, tempDir, cancellationToken);
        File.Delete(packageFile);

        if (!OperatingSystem.IsWindows())
            MarkExecutable(Path.Combine(tempDir, "tools"));

        Directory.Move(tempDir, installDir);
        return toolsDir;
    }

    [UnsupportedOSPlatform("windows")]
    private static void MarkExecutable(string toolsDir)
    {
        // Zip extraction drops the Unix execute bit that the shell launcher and native helpers need.
        const UnixFileMode executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

        foreach (string file in Directory.EnumerateFiles(toolsDir, "*", SearchOption.AllDirectories))
        {
            if (Path.GetExtension(file) is "" or ".sh" or ".so" or ".dylib")
                File.SetUnixFileMode(file, executable);
        }
    }

    private static string GetRuntimeSuffix()
    {
        string arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            Architecture other => throw new PlatformNotSupportedException($"Unsupported architecture {other}"),
        };

        if (OperatingSystem.IsWindows())
            return $"windows-{arch}";
        if (OperatingSystem.IsMacOS())
            return $"macos-{arch}";

        bool isMusl = File.Exists("/etc/alpine-release");
        return isMusl ? $"linux-musl-{arch}" : $"linux-{arch}";
    }
}
