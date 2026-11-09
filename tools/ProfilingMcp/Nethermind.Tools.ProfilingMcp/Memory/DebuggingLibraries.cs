// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Nethermind.Tools.ProfilingMcp.Memory;

/// <summary>
/// Supplies ClrMD in a sidecar with a trusted copy of the runtime's debugging library (<c>libmscordaccore.so</c>, the DAC) for the target's .NET version.
/// </summary>
/// <remarks>
/// ClrMD loads the DAC into its own process, which in a sidecar runs as root. The target's own copy is under the target's control, so it is never
/// loaded. The sidecar uses its own runtime's DAC when the versions match, and otherwise one the host took from Microsoft's runtime pack for that
/// version on nuget.org, copied into the sidecar's volume, which the target cannot write to.
/// </remarks>
internal static partial class DebuggingLibraries
{
    /// <summary>Where the sidecar keeps DACs the host supplied, in its volume: <c>&lt;folder&gt;/&lt;rid&gt;/&lt;version&gt;/libmscordaccore.so</c>.</summary>
    public const string SidecarFolder = "/profmcp-home/dac";

    private const string Dac = "libmscordaccore.so";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>Tools that load the DAC: native_memory, and the heap tools when given a dump instead of a PID.</summary>
    public static bool AreNeededBy(string tool, IReadOnlyDictionary<string, System.Text.Json.JsonElement> arguments) =>
        tool == "native_memory"
        || tool is "heap_stats" or "heap_retention" && arguments.TryGetValue("target", out System.Text.Json.JsonElement target) && !HeapTarget.IsProcess(target.ToString(), out _);

    /// <summary>The .NET version and runtime identifier of a runtime loaded from <paramref name="coreclrPath"/>, or null when they cannot be told.</summary>
    /// <remarks>A shared framework lives in a folder named after its version; a self-contained app's runtime does not, and gets no DAC.</remarks>
    internal static (string Rid, string Version)? Runtime(string coreclrPath, bool musl, string architecture)
    {
        string? version = Path.GetFileName(Path.GetDirectoryName(coreclrPath.Replace('\\', '/'))?.Replace('\\', '/'));
        string? arch = architecture switch { "amd64" or "x64" => "x64", "arm64" => "arm64", _ => null };
        return version is not null && arch is not null && VersionPattern().IsMatch(version) ? ($"linux-{(musl ? "musl-" : "")}{arch}", version) : null;
    }

    /// <summary>Run on the host: copies the DACs for every .NET runtime the target's processes have loaded into the sidecar, downloading them once.</summary>
    public static async Task ProvideAsync(string[] engine, string sidecar, string architecture, string cacheRoot, CancellationToken cancellationToken)
    {
        // The sidecar's own server maps its runtime from /profmcp; everything else is the target's.
        string maps = await ContainerSidecar.DockerAsync(engine, "exec", sidecar, "sh", "-c",
            "cat /proc/[0-9]*/maps 2>/dev/null | grep -oE '/[^ ]*(libcoreclr\\.so|ld-musl-[a-z0-9_]+\\.so\\.1)$' | grep -v '^/profmcp/' | sort -u || true");
        string[] paths = maps.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool musl = Array.Exists(paths, static p => p.Contains("ld-musl-", StringComparison.Ordinal));
        foreach (string path in paths)
        {
            if (!path.EndsWith("/libcoreclr.so", StringComparison.Ordinal) || Runtime(path, musl, architecture) is not var (rid, version))
                continue;

            string target = $"{SidecarFolder}/{rid}/{version}";
            if (await ContainerSidecar.SucceedsAsync(engine, "exec", sidecar, "test", "-f", $"{target}/{Dac}"))
                continue;

            string local = await DownloadAsync(rid, version, cacheRoot, cancellationToken);
            await ContainerSidecar.DockerAsync(engine, "exec", sidecar, "mkdir", "-p", target);
            await ContainerSidecar.DockerAsync(engine, "cp", local, $"{sidecar}:{target}/{Dac}");
        }
    }

    /// <summary>Takes the DAC out of Microsoft's runtime pack for <paramref name="rid"/> and <paramref name="version"/>, caching it under <paramref name="cacheRoot"/>.</summary>
    private static async Task<string> DownloadAsync(string rid, string version, string cacheRoot, CancellationToken cancellationToken)
    {
        string folder = Path.Combine(cacheRoot, "dac", rid, version);
        string dac = Path.Combine(folder, Dac);
        if (File.Exists(dac))
            return dac;

        string package = $"microsoft.netcore.app.runtime.{rid}";
        Uri url = new($"https://api.nuget.org/v3-flatcontainer/{package}/{version}/{package}.{version}.nupkg");
        await using Stream download = await Http.GetStreamAsync(url, cancellationToken);
        await using MemoryStream buffer = new();
        await download.CopyToAsync(buffer, cancellationToken);
        using ZipArchive archive = new(buffer, ZipArchiveMode.Read);
        ZipArchiveEntry entry = archive.GetEntry($"runtimes/{rid}/native/{Dac}")
            ?? throw new InvalidOperationException($"The .NET {version} runtime pack for {rid} has no {Dac}.");

        Directory.CreateDirectory(folder);
        string partial = dac + $".{Guid.NewGuid():N}.partial";
        await using (Stream source = entry.Open())
        await using (FileStream file = File.Create(partial))
            await source.CopyToAsync(file, cancellationToken);
        File.Move(partial, dac, overwrite: true);
        return dac;
    }

    /// <summary>Run in the sidecar: the trusted DAC for a target runtime loaded from <paramref name="coreclrPath"/>.</summary>
    /// <exception cref="InvalidOperationException">No trusted DAC for that version is available.</exception>
    public static string TrustedDac(string coreclrPath)
    {
        string version = Path.GetFileName(Path.GetDirectoryName(coreclrPath)) ?? "";
        string own = Path.Combine(AppContext.BaseDirectory, Dac);
        return Choose(version, Environment.Version.ToString(3), own, Directory.Exists(SidecarFolder) ? Directory.GetFiles(SidecarFolder, Dac, SearchOption.AllDirectories) : [])
            ?? throw new InvalidOperationException(
                $"There is no trusted debugging library (DAC) for the target's .NET {(version.Length > 0 ? version : "runtime")} in the sidecar. The server supplies one from " +
                "Microsoft's runtime pack for a runtime installed as a shared framework; a self-contained app's runtime gets none, so ClrMD analysis is not available for it.");
    }

    /// <summary>Picks the DAC for <paramref name="version"/>: the sidecar's own when it runs that version, else a supplied one; never the target's.</summary>
    internal static string? Choose(string version, string ownVersion, string ownDac, IEnumerable<string> supplied)
    {
        if (version == ownVersion)
            return ownDac;

        foreach (string dac in supplied)
        {
            string? folder = Path.GetFileName(Path.GetDirectoryName(dac.Replace('\\', '/'))?.Replace('\\', '/'));
            if (folder == version && dac.EndsWith($"-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}/{version}/{Dac}", StringComparison.Ordinal))
                return dac;
        }

        return null;
    }

    /// <summary>A released or preview .NET version, as used in a runtime pack's name; anything else from the target is not used in a URL.</summary>
    [GeneratedRegex(@"^\d{1,3}\.\d{1,3}\.\d{1,4}(-[A-Za-z0-9.]{1,40})?$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
