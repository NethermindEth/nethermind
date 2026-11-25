// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Packaging.Signing;

namespace Nethermind.Tools.ProfilingMcp.Memory;

/// <summary>
/// Supplies ClrMD in a sidecar with a trusted copy of the runtime's debugging library (<c>libmscordaccore.so</c>, the DAC) for the target's .NET version.
/// </summary>
/// <remarks>
/// ClrMD loads the DAC into its own process, which in a sidecar runs as root. The target's own copy is under the target's control, so it is never
/// loaded. The sidecar uses its own runtime's DAC when the versions match, and otherwise one the host took from Microsoft's runtime pack for that
/// version on nuget.org, after checking the pack's signature, copied into the sidecar's volume, which the target cannot write to.
/// </remarks>
internal static partial class DebuggingLibraries
{
    /// <summary>Where the sidecar keeps DACs the host supplied, in its volume: <c>&lt;folder&gt;/&lt;rid&gt;/&lt;version&gt;/libmscordaccore.so</c>.</summary>
    public const string SidecarFolder = "/profmcp-home/dac";

    private const string Dac = "libmscordaccore.so";

    /// <summary>The subject of the certificate Microsoft signs its NuGet packages with.</summary>
    internal const string MicrosoftSigner = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";

    /// <summary>The SHA-256 hash of DigiCert Trusted Root G4, under which Microsoft's package signing certificates are issued.</summary>
    internal const string SigningRoot = "552F7BDCF1A7AF9E6CE672017F4F12ABF77240C78E761AC203D1D9D20AC89988";

    private const string CodeSigning = "1.3.6.1.5.5.7.3.3";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>
    /// Tools that load the DAC: native_memory, and the heap tools when given a dump instead of a PID. write_dump is included because
    /// DACs are supplied for the runtimes running at the time, and the dumped process may have exited by the time its dump is read.
    /// </summary>
    public static bool AreNeededBy(string tool, IReadOnlyDictionary<string, JsonElement> arguments) =>
        tool is "native_memory" or "write_dump"
        || tool is "heap_stats" or "heap_retention" && arguments.TryGetValue("target", out JsonElement target) && !HeapTarget.IsProcess(target.ToString(), out _);

    /// <summary>The .NET version and runtime identifier of a runtime loaded from <paramref name="coreclrPath"/>, or null when they cannot be told.</summary>
    /// <remarks>A shared framework lives in a folder named after its version; a self-contained app's runtime does not, and gets no DAC.</remarks>
    internal static (string Rid, string Version)? Runtime(string coreclrPath, string architecture)
    {
        string? version = Path.GetFileName(Path.GetDirectoryName(coreclrPath.Replace('\\', '/'))?.Replace('\\', '/'));
        string? arch = architecture switch { "amd64" or "x64" => "x64", "arm64" => "arm64", _ => null };
        return version is not null && arch is not null && VersionPattern().IsMatch(version) ? ($"linux-{arch}", version) : null;
    }

    /// <summary>
    /// Lists the .NET runtimes the target's processes have loaded, one per line as <c>glibc|musl &lt;path of libcoreclr.so&gt;</c>.
    /// The sidecar's own server maps its runtime from /profmcp; everything else is the target's.
    /// </summary>
    private const string RuntimesScript =
        "for m in /proc/[0-9]*/maps; do " +
        "c=$(grep -oE '/[^ ]*/libcoreclr\\.so$' \"$m\" 2>/dev/null | head -n 1); " +
        "case \"$c\" in ''|/profmcp/*) continue;; esac; " +
        "if grep -q '/ld-musl-' \"$m\" 2>/dev/null; then echo \"musl $c\"; else echo \"glibc $c\"; fi; " +
        "done | sort -u";

    /// <summary>Run on the host: copies the DACs for every .NET runtime the target's processes have loaded into the sidecar, downloading them once.</summary>
    /// <returns>The runtimes whose DAC could not be supplied and why, or null when every one was.</returns>
    /// <remarks>
    /// Nothing is supplied for a runtime the sidecar's own DAC serves, nor for a musl one, whose DAC the glibc sidecar cannot load; the tools
    /// report that themselves when they need it. A DAC is copied under a temporary name and renamed, so an interrupted copy is never taken for one.
    /// One runtime's DAC failing to download does not stop the others: the call may need only one of them.
    /// </remarks>
    public static async Task<string?> ProvideAsync(string[] engine, string sidecar, string architecture, string cacheRoot, CancellationToken cancellationToken)
    {
        List<string> problems = [];
        string? own = null;
        foreach ((string path, bool musl) in ParseRuntimes(await ContainerSidecar.DockerAsync(engine, ["exec", sidecar, "sh", "-c", RuntimesScript], cancellationToken)))
        {
            if (musl || Runtime(path, architecture) is not var (rid, version))
                continue;

            own ??= await OwnVersionAsync(engine, sidecar, cancellationToken) ?? "";
            string target = $"{SidecarFolder}/{rid}/{version}";
            if (version == own || await ContainerSidecar.SucceedsAsync(engine, ["exec", sidecar, "test", "-f", $"{target}/{Dac}"], cancellationToken))
                continue;

            if (await TrySupplyAsync(() => SupplyAsync(engine, sidecar, rid, version, target, cacheRoot, cancellationToken), cancellationToken) is { } problem)
                problems.Add($".NET {version}: {problem}");
        }

        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    /// <summary>Runs <paramref name="supply"/> for one runtime, returning why it failed instead of throwing, so the others are still supplied.</summary>
    /// <remarks>
    /// <see cref="HttpClient"/> reports its own timeout as an <see cref="OperationCanceledException"/>, which is one runtime's failure like any
    /// other; only the caller's cancellation ends the whole preparation.
    /// </remarks>
    internal static async Task<string?> TrySupplyAsync(Func<Task> supply, CancellationToken cancellationToken)
    {
        try
        {
            await supply();
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return e.Message;
        }
    }

    private static async Task SupplyAsync(string[] engine, string sidecar, string rid, string version, string target, string cacheRoot, CancellationToken cancellationToken)
    {
        string local = await DownloadAsync(rid, version, cacheRoot, cancellationToken);
        string partial = $"{target}/{Dac}.{Guid.NewGuid():N}.partial";
        await ContainerSidecar.DockerAsync(engine, ["exec", sidecar, "mkdir", "-p", target], cancellationToken);
        try
        {
            await ContainerSidecar.DockerAsync(engine, ["cp", local, $"{sidecar}:{partial}"], cancellationToken);
            await ContainerSidecar.DockerAsync(engine, ["exec", sidecar, "mv", "-f", partial, $"{target}/{Dac}"], cancellationToken);
        }
        catch
        {
            // Removing the partial copy is needed most after a cancellation, so it does not take the token.
            await ContainerSidecar.SucceedsAsync(engine, ["exec", sidecar, "rm", "-f", partial], CancellationToken.None);
            throw;
        }
    }

    /// <summary>Reads the output of <see cref="RuntimesScript"/>.</summary>
    internal static IEnumerable<(string Path, bool Musl)> ParseRuntimes(string output)
    {
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space > 0 && line[..space] is "glibc" or "musl")
                yield return (line[(space + 1)..], line[..space] == "musl");
        }
    }

    /// <summary>The .NET version of the sidecar's own runtime, whose DAC it bundles, or null when its image does not tell.</summary>
    private static async Task<string?> OwnVersionAsync(string[] engine, string sidecar, CancellationToken cancellationToken)
    {
        try
        {
            return IncludedRuntimeVersion(await ContainerSidecar.DockerAsync(engine, ["exec", sidecar, "cat", $"{ContainerSidecar.ServerPath}.runtimeconfig.json"], cancellationToken));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The version of the .NET runtime a self-contained app's <c>runtimeconfig.json</c> says it includes, or null.</summary>
    internal static string? IncludedRuntimeVersion(string runtimeConfig)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(runtimeConfig);
            if (!document.RootElement.TryGetProperty("runtimeOptions", out JsonElement options)
                || !options.TryGetProperty("includedFrameworks", out JsonElement frameworks)
                || frameworks.ValueKind != JsonValueKind.Array)
                return null;

            foreach (JsonElement framework in frameworks.EnumerateArray())
            {
                if (framework.TryGetProperty("name", out JsonElement name) && name.ValueEquals("Microsoft.NETCore.App")
                    && framework.TryGetProperty("version", out JsonElement version))
                    return version.GetString();
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Refuses a target on musl libc, whose runtime's DAC the glibc sidecar cannot load.</summary>
    /// <param name="modules">The paths of the target's loaded native modules.</param>
    /// <exception cref="InvalidOperationException">The target loaded musl's dynamic loader.</exception>
    public static void RequireGlibc(IEnumerable<string> modules)
    {
        foreach (string module in modules)
        {
            if (module.Contains("/ld-musl-", StringComparison.Ordinal))
                throw new InvalidOperationException("The target runs on musl libc (an Alpine-based image). The sidecar is a glibc image and cannot load a musl runtime's " +
                                                    "debugging library, so native_memory's managed breakdown and dump analysis are not available for it; the other tools are.");
        }
    }

    /// <summary>Takes the DAC out of Microsoft's runtime pack for <paramref name="rid"/> and <paramref name="version"/>, once its signature is checked, caching it under <paramref name="cacheRoot"/>.</summary>
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
        await VerifyAsync(buffer, cancellationToken);
        buffer.Position = 0;
        using ZipArchive archive = new(buffer, ZipArchiveMode.Read);
        ZipArchiveEntry entry = archive.GetEntry($"runtimes/{rid}/native/{Dac}")
            ?? throw new InvalidOperationException($"The .NET {version} runtime pack for {rid} has no {Dac}.");

        Directory.CreateDirectory(folder);
        await using (Stream source = entry.Open())
            await SaveAsync(source, dac, cancellationToken);
        return dac;
    }

    /// <summary>Checks that a runtime pack is signed by Microsoft and has not changed since it was signed.</summary>
    /// <remarks>
    /// A NuGet package's signature covers a hash of its content. The signing certificate must be Microsoft's and chain to <see cref="SigningRoot"/>,
    /// which is pinned, so the DAC does not rest on HTTPS and the host's certificate store alone. Expired certificates are accepted, since older
    /// runtime packs were signed with ones that have since expired, and revocation is not checked.
    /// </remarks>
    /// <exception cref="InvalidDataException">The package is not signed, does not match its signature, or was not signed by Microsoft.</exception>
    internal static async Task VerifyAsync(Stream package, CancellationToken cancellationToken)
    {
        using PackageArchiveReader reader = new(package, leaveStreamOpen: true);
        PrimarySignature signature;
        try
        {
            signature = await reader.GetPrimarySignatureAsync(cancellationToken) ?? throw new InvalidDataException("The runtime pack is not signed.");
            signature.SignedCms.CheckSignature(verifySignatureOnly: true);
            await reader.ValidateIntegrityAsync(signature.SignatureContent, cancellationToken);
        }
        catch (Exception e) when (e is CryptographicException or PackagingException)
        {
            throw new InvalidDataException($"The runtime pack's signature does not check out: {e.Message}", e);
        }

        CheckSigner(signature.SignerInfo.Certificate, signature.SignedCms.Certificates);
    }

    /// <summary>Checks that <paramref name="signer"/> is Microsoft's code signing certificate, issued under <see cref="SigningRoot"/>.</summary>
    /// <param name="certificates">The certificates the signature carries, which include the chain up to the root.</param>
    /// <exception cref="InvalidDataException">It is not.</exception>
    internal static void CheckSigner(X509Certificate2? signer, X509Certificate2Collection certificates)
    {
        if (signer?.Subject != MicrosoftSigner)
            throw new InvalidDataException($"The runtime pack is signed by {signer?.Subject ?? "no one"}, not by Microsoft.");

        X509Certificate2? root = null;
        foreach (X509Certificate2 certificate in certificates)
        {
            if (certificate.GetCertHashString(HashAlgorithmName.SHA256) == SigningRoot)
                root = certificate;
        }

        using X509Chain chain = new();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        if (root is not null)
            chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.ExtraStore.AddRange(certificates);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(CodeSigning));
        if (root is null || !chain.Build(signer))
            throw new InvalidDataException("The runtime pack's signing certificate is not a code signing certificate issued under DigiCert Trusted Root G4, which Microsoft's are.");
    }

    /// <summary>Writes <paramref name="source"/> to <paramref name="path"/> under a temporary name and renames it, so a failed write leaves nothing behind.</summary>
    internal static async Task SaveAsync(Stream source, string path, CancellationToken cancellationToken)
    {
        string partial = path + $".{Guid.NewGuid():N}.partial";
        try
        {
            await using (FileStream file = File.Create(partial))
                await source.CopyToAsync(file, cancellationToken);
            File.Move(partial, path, overwrite: true);
        }
        finally
        {
            File.Delete(partial);
        }
    }

    /// <summary>Run in the sidecar: the trusted DAC for a target runtime loaded from <paramref name="coreclrPath"/>.</summary>
    /// <exception cref="InvalidOperationException">No trusted DAC for that version is available.</exception>
    public static string TrustedDac(string coreclrPath) => TrustedDac(coreclrPath, AppContext.BaseDirectory, SidecarFolder);

    /// <param name="serverFolder">The sidecar server's folder, with its runtime's DAC and <c>runtimeconfig.json</c>.</param>
    /// <param name="suppliedFolder">Where the host supplied DACs, as <see cref="SidecarFolder"/>.</param>
    internal static string TrustedDac(string coreclrPath, string serverFolder, string suppliedFolder)
    {
        string version = Path.GetFileName(Path.GetDirectoryName(coreclrPath)) ?? "";
        string own = Path.Combine(serverFolder, Dac);
        string ownVersion = OwnVersion(Path.Combine(serverFolder, $"{Path.GetFileName(ContainerSidecar.ServerPath)}.runtimeconfig.json"));
        return Choose(version, ownVersion, own, Directory.Exists(suppliedFolder) ? Directory.GetFiles(suppliedFolder, Dac, SearchOption.AllDirectories) : [])
            ?? throw new InvalidOperationException(
                $"There is no trusted debugging library (DAC) for the target's .NET {(version.Length > 0 ? version : "runtime")} in the sidecar. The server supplies one from " +
                "Microsoft's runtime pack for a runtime installed as a shared framework; a self-contained app's runtime gets none, so ClrMD analysis is not available for it.");
    }

    /// <summary>The sidecar's own .NET version as its <c>runtimeconfig.json</c> names it, the version the host skips supplying a DAC for.</summary>
    /// <remarks><see cref="Environment.Version"/> drops a preview's suffix, so it is used only when the file does not name a version.</remarks>
    internal static string OwnVersion(string runtimeConfigPath) =>
        (File.Exists(runtimeConfigPath) ? IncludedRuntimeVersion(File.ReadAllText(runtimeConfigPath)) : null) ?? Environment.Version.ToString(3);

    /// <summary>Picks the DAC for <paramref name="version"/>: the sidecar's own when it runs that version, else a supplied one; never the target's.</summary>
    internal static string? Choose(string version, string ownVersion, string ownDac, IEnumerable<string> supplied)
    {
        if (version == ownVersion)
            return ownDac;

        foreach (string dac in supplied)
        {
            string? folder = Path.GetFileName(Path.GetDirectoryName(dac.Replace('\\', '/'))?.Replace('\\', '/'));
            if (folder == version && dac.EndsWith($"/linux-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}/{version}/{Dac}", StringComparison.Ordinal))
                return dac;
        }

        return null;
    }

    /// <summary>A released or preview .NET version, as used in a runtime pack's name; anything else from the target is not used in a URL.</summary>
    [GeneratedRegex(@"^\d{1,3}\.\d{1,3}\.\d{1,4}(-[A-Za-z0-9.]{1,40})?$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
