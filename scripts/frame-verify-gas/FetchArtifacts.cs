#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

#if !SCRIPT_TEST
using System.Runtime.InteropServices;

using var cancellation = new CancellationTokenSource();
using var termination = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; cancellation.Cancel(); });
Console.CancelKeyPress += (_, context) => { context.Cancel = true; cancellation.Cancel(); };
using var handler = new HttpClientHandler { AllowAutoRedirect = false };
using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
return await ArtifactFetch.Run(http, Environment.GetEnvironmentVariable("REQUESTED_VERSION") ?? "", Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? "", Environment.GetEnvironmentVariable("GITHUB_ENV") ?? "", Environment.GetEnvironmentVariable("GH_TOKEN") ?? "", cancellation.Token);
#endif

internal static partial class ArtifactFetch
{
    [GeneratedRegex(@"\s")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\Av(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z")]
    private static partial Regex ReleaseVersion();

    [GeneratedRegex(@"\A[0-9a-f]{64} [ *]sweep-[a-z0-9]+\.tar\.gz\z")]
    private static partial Regex ChecksumLine();

    [GeneratedRegex(@"\A(?:\./|(?:\./)?(?<sweep>[A-Za-z0-9._-]+)(?:/[A-Za-z0-9._-]+)*/?)\z")]
    private static partial Regex ArchivePath();

    internal const string Repository = "NethermindEth/frame-verify-gas";
    // This sweep represents a real pool; the other sweeps are synthetic ceiling controls.
    // Pin its upstream commit so releases cannot silently change the measured profile.
    internal const string SoispokeCommit = "6dedda193bf09c9d80cd89b3dc23eccf580d1026";
    internal static readonly string[] Labels = ["250k", "300k", "400k", "500k", "soispoke"];

    internal static async Task<int> Run(HttpClient http, string version, string temporary, string environmentFile, string token, CancellationToken cancellationToken)
    {
        string? work = null;
        try
        {
            version = Whitespace().Replace(version, "");
            if (version.Length == 0) throw new IOException($"groth16_artifacts_version is empty. Supply a published {Repository} release tag (vMAJOR.MINOR.PATCH).");
            if (!ReleaseVersion().IsMatch(version)) throw new IOException($"groth16_artifacts_version '{version}' is not a vMAJOR.MINOR.PATCH release tag.");
            if (temporary.Length == 0) throw new IOException("RUNNER_TEMP is not set.");
            if (environmentFile.Length == 0) throw new IOException("GITHUB_ENV is not set.");
            work = Path.Combine(Path.GetFullPath(temporary), "frame-verify-gas-download." + Guid.NewGuid().ToString("N"));
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(work);
            else Directory.CreateDirectory(work, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var assets = Path.Combine(work, "assets");
            Directory.CreateDirectory(assets);
            var releasePath = Path.Combine(work, "release.json");
            try { await Download(new Uri($"https://api.github.com/repos/{Repository}/releases/tags/{version}"), releasePath, true); }
            catch (Exception exception) when (exception is HttpRequestException or IOException || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            { throw new IOException($"Release {version} could not be read from {Repository} (a nonexistent, draft or not-yet-published release looks identical here): {exception.Message}", exception); }
            using var release = JsonDocument.Parse(await File.ReadAllTextAsync(releasePath, cancellationToken));
            var root = release.RootElement;
            if (root.GetProperty("prerelease").GetBoolean()) throw new IOException($"Release {version} on {Repository} is a prerelease. Supply a full release.");
            // The tags endpoint normally returns 404 for drafts rather than exposing this state.
            if (root.GetProperty("draft").GetBoolean()) throw new IOException($"Release {version} on {Repository} has unexpected draft/prerelease state 'true false'.");
            var urls = new Dictionary<string, string>();
            if (root.TryGetProperty("assets", out var list))
                foreach (var asset in list.EnumerateArray()) urls.TryAdd(asset.GetProperty("name").GetString()!, asset.GetProperty("browser_download_url").GetString()!);
            var tarballs = Labels.Select(label => "sweep-" + label + ".tar.gz").ToArray();
            foreach (var asset in new[] { "SHA256SUMS" }.Concat(tarballs))
            {
                if (!urls.TryGetValue(asset, out var url) || url.Length == 0) throw new IOException($"Release {version} on {Repository} has no {asset} asset.");
                try { await Download(new Uri(url), Path.Combine(assets, asset), false); }
                catch (Exception exception) when (exception is HttpRequestException or IOException || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                { throw new IOException($"Downloading {asset} from release {version} of {Repository} failed: {exception.Message}", exception); }
            }
            var sums = Path.Combine(assets, "SHA256SUMS");
            var lines = await File.ReadAllLinesAsync(sums, cancellationToken);
            if (lines.Length != tarballs.Length || lines.Any(line => !ChecksumLine().IsMatch(line)) || !lines.Select(line => line[66..]).Order(StringComparer.Ordinal).SequenceEqual(tarballs.Order(StringComparer.Ordinal)))
                throw new IOException($"SHA256SUMS of release {version} must list exactly: {string.Join(' ', tarballs.Order(StringComparer.Ordinal))}.");
            foreach (var line in lines)
                if (await Hash(Path.Combine(assets, line[66..])) != line[..64]) throw new IOException($"The downloaded tarballs do not match SHA256SUMS of release {version}; the download is corrupt, partial or was altered. Re-dispatch.");
            var artifacts = Path.Combine(temporary, "frame-verify-gas-groth16");
            if (Directory.Exists(artifacts)) Directory.Delete(artifacts, recursive: true);
            else if (File.Exists(artifacts)) File.Delete(artifacts);
            Directory.CreateDirectory(artifacts);
            foreach (var label in Labels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sweep = "sweep-" + label;
                var archive = Path.Combine(assets, sweep + ".tar.gz");
                try { ValidateArchive(archive, sweep); }
                catch (InvalidDataException exception)
                {
                    throw new IOException($"{sweep}.tar.gz is not a readable, non-empty gzip tarball; the download is corrupt.", exception);
                }
                var staging = Path.Combine(work, sweep);
                Directory.CreateDirectory(staging);
                using (var source = File.OpenRead(archive))
                using (var gzip = new GZipStream(source, CompressionMode.Decompress))
                using (var reader = new TarReader(gzip))
                    while (reader.GetNextEntry() is { } entry)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var path = Path.Combine(staging, entry.Name);
                        if (entry.EntryType == TarEntryType.Directory) Directory.CreateDirectory(path);
                        else
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                            using var output = File.Create(path);
                            if (entry.DataStream is { } data) await data.CopyToAsync(output, cancellationToken);
                        }
                    }
                if (!Directory.Exists(Path.Combine(staging, sweep)) || Directory.EnumerateFileSystemEntries(staging).Count() != 1) throw new IOException($"{sweep}.tar.gz did not extract to plain files under {sweep}/ only.");
                Directory.Move(Path.Combine(staging, sweep), Path.Combine(artifacts, sweep));
                foreach (var file in new[] { "verifier.hex", "calldata-invalid.hex", "gas.txt" })
                    if (!File.Exists(Path.Combine(artifacts, sweep, file))) throw new IOException($"{sweep}.tar.gz has no {sweep}/{file}.");
            }
            try
            {
                string provenanceText;
                try { provenanceText = await File.ReadAllTextAsync(Path.Combine(artifacts, "sweep-soispoke", "provenance.json"), cancellationToken); }
                catch (IOException exception) { throw new IOException($"sweep-soispoke/provenance.json is missing or unparsable ({exception.GetType().Name})", exception); }
                using var provenance = JsonDocument.Parse(provenanceText);
                if (!provenance.RootElement.TryGetProperty("commit", out var commit) || commit.ValueKind != JsonValueKind.String || commit.GetString() != SoispokeCommit)
                    throw new IOException($"sweep-soispoke/provenance.json names upstream commit '{(commit.ValueKind == JsonValueKind.Undefined ? "None" : commit.ToString())}', not {SoispokeCommit}");
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
            { throw new IOException($"Release {version} of {Repository} does not carry the pinned soispoke verifier: {exception.Message}", exception); }
            if (CheckVerifiers(artifacts) != 0) throw new IOException($"The plausibility heuristic in scripts/frame-verify-gas/FetchArtifacts.cs rejected release {version}; see the errors above.");
            var savedSums = Path.Combine(temporary, "frame-verify-gas-groth16.SHA256SUMS");
            File.Copy(sums, savedSums, overwrite: true);
            var digest = await Hash(sums);
            try { await File.AppendAllTextAsync(environmentFile, $"FRAME_GROTH16_ARTIFACTS={artifacts}\nGROTH16_ARTIFACTS_SHA256SUMS={savedSums}\nGROTH16_ARTIFACTS_VERSION={version}\nGROTH16_ARTIFACTS_SHA256SUMS_DIGEST={digest}\nGROTH16_SOISPOKE_COMMIT={SoispokeCommit}\n", cancellationToken); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { throw new IOException("Failed to write artifact paths to GITHUB_ENV; the measurement step would otherwise run without FRAME_GROTH16_ARTIFACTS set and self-ignore the privacy cases silently.", exception); }
            Console.WriteLine($"Groth16 artifacts of {Repository}@{version} extracted to {artifacts}.");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or FormatException or UnauthorizedAccessException or JsonException or HttpRequestException or OperationCanceledException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        { Console.WriteLine("::error::" + exception.Message); return 1; }
        finally { if (work is not null && Directory.Exists(work)) Directory.Delete(work, recursive: true); }

        async Task<string> Hash(string path)
        {
            await using var source = File.OpenRead(path);
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(source, cancellationToken));
        }

        async Task Download(Uri address, string output, bool release)
        {
            for (var attempt = 0; ; attempt++)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(release ? 30 : 60));
                try
                {
                    var target = address;
                    for (var redirects = 0; ; redirects++)
                    {
                        if (target.Scheme != Uri.UriSchemeHttps) throw new IOException("Only HTTPS downloads and redirects are allowed.");
                        using var request = new HttpRequestMessage(HttpMethod.Get, target);
                        request.Headers.UserAgent.ParseAdd("Nethermind-verifier-artifacts");
                        // Public releases need no token; authentication only raises the API rate limit.
                        if (release && token.Length > 0 && target.Authority == address.Authority)
                            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                        if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                        {
                            if (redirects >= 50 || response.Headers.Location is null) throw new HttpRequestException("Too many redirects or missing redirect location.");
                            target = new Uri(target, response.Headers.Location);
                            continue;
                        }
                        response.EnsureSuccessStatusCode();
                        await using var file = File.Create(output);
                        await response.Content.CopyToAsync(file, timeout.Token);
                        return;
                    }
                }
                catch (Exception exception) when (attempt < 3 && !cancellationToken.IsCancellationRequested && IsTransient(exception, release))
                {
                    Console.Error.WriteLine($"Download retry {attempt + 1}: {exception.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(1 << attempt), cancellationToken);
                }
            }
        }
    }

    [GeneratedRegex(@"\A[1-9][0-9]{0,8}\z")]
    private static partial Regex PositiveInteger();

    [GeneratedRegex(@"\A0x(?:[0-9a-fA-F]{2})+\z")]
    private static partial Regex HexBytes();

    private static int CheckVerifiers(string root)
    {
        var failures = new List<string>();
        foreach (var label in Labels)
        {
            var sweep = "sweep-" + label;
            var verifier = sweep + "/verifier.hex";
            var code = ReadHex(verifier);
            if (code is not null)
            {
                if (code.Length is < 1024 or > 24576) Reject(verifier, "size", $"{code.Length} bytes, outside 1024..24576 (EIP-170)");
                var pushed = new HashSet<byte>();
                var staticCall = false;
                for (var pc = 0; pc < code.Length;)
                {
                    var op = code[pc];
                    if (op == 0x60 && pc + 1 < code.Length) pushed.Add(code[pc + 1]);
                    staticCall |= op == 0xfa;
                    pc += 1 + (op is >= 0x60 and <= 0x7f ? op - 0x60 + 1 : 0);
                }
                var missing = new List<string>();
                foreach (var (address, name) in new[] { (6, "ecAdd"), (7, "ecMul"), (8, "ecPairing") })
                    if (!pushed.Contains((byte)address)) missing.Add($"0x{address:x2} {name}");
                if (missing.Count > 0) Reject(verifier, "precompile-push", "no PUSH1 of " + string.Join(", ", missing));
                if (!staticCall) Reject(verifier, "staticcall", "no STATICCALL opcode");
            }
            var calldata = sweep + "/calldata-invalid.hex";
            if (ReadHex(calldata) is { Length: < 4 }) Reject(calldata, "length", "shorter than a function selector");
            if (!PositiveInteger().IsMatch(Read(sweep + "/gas.txt"))) Reject(sweep + "/gas.txt", "integer", "not a positive integer");
        }
        foreach (var failure in failures) Console.WriteLine("::error::" + failure);
        return failures.Count > 0 ? 1 : 0;

        void Reject(string path, string check, string detail) => failures.Add($"FetchArtifacts.cs heuristic rejected {path} ({check} check): {detail}");
        string Read(string path) => Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(root, path))).Trim();
        byte[]? ReadHex(string path)
        {
            var text = Read(path);
            if (HexBytes().IsMatch(text)) return Convert.FromHexString(text[2..]);
            Reject(path, "hex", "not 0x-prefixed, byte-aligned hex");
            return null;
        }
    }

    private static bool IsTransient(Exception exception, bool release) => exception switch
    {
        OperationCanceledException or HttpIOException => true,
        HttpRequestException error => !release || error.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)error.StatusCode >= 500,
        _ => false
    };

    private static void ValidateArchive(string archive, string sweep)
    {
        const long maximum = 16 * 1024 * 1024;
        using var source = File.OpenRead(archive);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        var count = 0;
        var expanded = 0L;
        while (reader.GetNextEntry() is { } entry)
        {
            count++;
            var path = ArchivePath().Match(entry.Name);
            if (!path.Success || entry.Name != "./" && path.Groups["sweep"].Value != sweep || entry.Name.Split('/').Contains("..", StringComparer.Ordinal))
                throw new IOException($"{sweep}.tar.gz has a member with an unexpected path or prefix ('{JsonEncodedText.Encode(entry.Name)}'); only {sweep}/ or ./{sweep}/ without '..' is allowed.");
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory)) throw new IOException($"{sweep}.tar.gz has a link or special-file member; only regular files and directories are allowed.");
            expanded = checked(expanded + entry.Length);
            if (expanded > maximum) throw new IOException($"{sweep}.tar.gz expands to {expanded} bytes, over the {maximum}-byte limit.");
        }
        gzip.CopyTo(Stream.Null);
        if (count == 0) throw new IOException($"{sweep}.tar.gz is not a readable, non-empty gzip tarball; the download is corrupt.");
    }
}
