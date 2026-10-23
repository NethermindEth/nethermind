#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:property DefineConstants=SCRIPT_TEST
#:include FetchArtifacts.cs

using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

var root = Directory.CreateTempSubdirectory("verifier-tests-").FullName;
using var handler = new Releases();
using var http = new HttpClient(handler);
var passed = 0;
var failures = new List<string>();
var good = "0x600660076008fa" + Repeat("5b", 1100) + "\n";
var files = new Dictionary<string, byte[]>();
foreach (var label in ArtifactFetch.Labels)
{
    files[$"sweep-{label}/verifier.hex"] = Encoding.ASCII.GetBytes(good);
    files[$"sweep-{label}/calldata-invalid.hex"] = Encoding.ASCII.GetBytes("0x" + Repeat("00", 36) + "\n");
    files[$"sweep-{label}/gas.txt"] = "234190\n"u8.ToArray();
}
files["sweep-soispoke/provenance.json"] = Encoding.UTF8.GetBytes(new JsonObject { ["commit"] = ArtifactFetch.SoispokeCommit }.ToJsonString());
try
{
    Release("v1.0.0");
    Release("v1.0.1", members: entries => entries.Insert(0, new UstarTarEntry(TarEntryType.Directory, "./")));
    Release("v1.0.2", members: entries => { foreach (var entry in entries) entry.Name = "./" + entry.Name; });
    Release("v1.1.0", draft: true); Release("v1.2.0", prerelease: true);
    Release("v2.0.0", assets: data => data["sweep-300k.tar.gz"] = [.. data["sweep-300k.tar.gz"], (byte)'x']);
    Release("v2.0.1", assets: data => ChangeSums(data, lines => lines[..^1]));
    Release("v2.0.2", assets: data => ChangeSums(data, lines => [.. lines, Repeat("0", 64) + "  sweep-evil.tar.gz"]));
    Release("v2.0.3", assets: data => ChangeSums(data, lines => [lines[0], .. lines[..^1]]));
    Release("v2.0.4", assets: data => data.Remove("SHA256SUMS"));
    Release("v2.0.5", assets: data => data.Remove("sweep-500k.tar.gz"));
    var escaped = Path.Combine(root, "escaped").Replace('\\', '/');
    foreach (var (version, path) in new[] { ("v3.0.0", "sweep-250k/../gas.txt"), ("v3.0.1", escaped), ("v3.0.2", "sweep-300k/gas.txt"), ("v3.0.3", "gas.txt"), ("v3.0.8", "sweep-250k/evil\nname") })
        Release(version, members: entries => entries.Single(entry => entry.Name == "sweep-250k/gas.txt").Name = path);
    foreach (var (version, type, path) in new[] { ("v3.0.4", TarEntryType.SymbolicLink, "sweep-250k/verifier.hex"), ("v3.0.5", TarEntryType.SymbolicLink, "sweep-250k/source"), ("v3.0.6", TarEntryType.HardLink, "sweep-250k/gas-link.txt"), ("v3.0.7", TarEntryType.Fifo, "sweep-250k/pipe") })
        Release(version, members: entries =>
        {
            var previous = entries.Find(entry => entry.Name == path);
            if (previous is not null) { previous.DataStream?.Dispose(); entries.Remove(previous); }
            var entry = new UstarTarEntry(type, path);
            if (type is TarEntryType.SymbolicLink or TarEntryType.HardLink) entry.LinkName = "/tmp";
            entries.Add(entry);
        });
    Release("v3.0.9", contents: data => data["sweep-250k/padding.bin"] = new byte[16 * 1024 * 1024 + 1]);
    Release("v3.0.10", contents: data => data.Remove("sweep-250k/gas.txt"));
    foreach (var (version, path, value) in new[]
    {
        ("v4.0.0", "verifier.hex", "0x" + Repeat("5b", 1100)),
        ("v4.0.1", "verifier.hex", "0x" + Repeat("616006", 150) + Repeat("616007", 150) + Repeat("616008", 150) + "fa"),
        ("v4.0.2", "verifier.hex", "0x600660076008f1" + Repeat("5b", 1100)),
        ("v4.0.3", "verifier.hex", "0x600660076008fa" + Repeat("5b", 100)),
        ("v4.0.4", "verifier.hex", "0x600660076008fa" + Repeat("5b", 24600)),
        ("v4.0.5", "verifier.hex", "0x600660076008f"), ("v4.0.6", "calldata-invalid.hex", "0xzz"),
        ("v4.0.7", "calldata-invalid.hex", "0x0102"), ("v4.0.8", "gas.txt", "12a4")
    }) Release(version, contents: data => data["sweep-250k/" + path] = Encoding.ASCII.GetBytes(value));
    Release("v5.0.0", contents: data => data.Remove("sweep-soispoke/provenance.json"));
    Release("v5.0.1", contents: data => data["sweep-soispoke/provenance.json"] = "{\"commit\":\"16b88a3\"}"u8.ToArray());

    var happy = await Expect("happy-path", "v1.0.0", 0, "extracted to");
    Check("one release lookup; token only on API request", handler.Requests.Count == 7 && handler.Requests[0].Authorization == "Bearer stub" && handler.Requests.Skip(1).All(request => request.Authorization is null));
    var environment = File.ReadAllText(Path.Combine(happy.Run, "github_env"));
    var artifacts = Path.Combine(happy.Run, "temp", "frame-verify-gas-groth16");
    Check("happy-path-env", environment.Contains($"FRAME_GROTH16_ARTIFACTS={artifacts}\n") && environment.Contains("GROTH16_ARTIFACTS_VERSION=v1.0.0\n") && ChecksumsDigest().IsMatch(environment));
    Check("happy-path-tree", files.All(pair => File.ReadAllBytes(Path.Combine(artifacts, pair.Key)).SequenceEqual(pair.Value)) && Directory.GetFiles(artifacts, "*", SearchOption.AllDirectories).Length == files.Count && environment.Contains($"GROTH16_ARTIFACTS_SHA256SUMS={Path.Combine(happy.Run, "temp", "frame-verify-gas-groth16.SHA256SUMS")}\n") && File.ReadAllBytes(Path.Combine(happy.Run, "temp", "frame-verify-gas-groth16.SHA256SUMS")).SequenceEqual(handler.Assets["v1.0.0"]["SHA256SUMS"]));
    Check("happy-path-no-download-dir-left", Directory.GetDirectories(Path.Combine(happy.Run, "temp"), "frame-verify-gas-download.*").Length == 0);
    await Expect("happy-path-no-token", "v1.0.0", 0, "extracted to", "");
    Check("happy-path-no-token:no-auth-header", handler.Requests.All(request => request.Authorization is null));
    await Expect("dot-slash-root", "v1.0.1", 0, "extracted to");
    await Expect("dot-slash-prefix", "v1.0.2", 0, "extracted to");
    await Expect("draft", "v1.1.0", 1, "could not be read from");
    await Expect("prerelease", "v1.2.0", 1, "is a prerelease");
    await Expect("nonexistent-tag", "v9.9.9", 1, "could not be read from");
    foreach (var bad in new[] { "latest", "v1.2", "v1.2.3-rc1", "../x", "v01.2.3", "1.2.3", "v1.2.3;id" })
    {
        await Expect("bad-version:" + bad, bad, 1, "is not a vMAJOR.MINOR.PATCH release tag");
        Check("bad-version:" + bad + ":no-download-call", handler.Requests.Count == 0);
    }
    foreach (var (name, version, pattern) in new[]
    {
        ("empty-version", "", "groth16_artifacts_version is empty"), ("blank-version", "  ", "groth16_artifacts_version is empty"),
        ("tampered-tarball", "v2.0.0", "do not match SHA256SUMS .* corrupt, partial or was altered"),
        ("sums-missing-entry", "v2.0.1", "SHA256SUMS of release v2.0.1 must list exactly"),
        ("sums-extra-entry", "v2.0.2", "SHA256SUMS of release v2.0.2 must list exactly"),
        ("sums-duplicate-entry", "v2.0.3", "SHA256SUMS of release v2.0.3 must list exactly"),
        ("sums-asset-missing", "v2.0.4", "has no SHA256SUMS asset"), ("tarball-asset-missing", "v2.0.5", "has no sweep-500k.tar.gz asset"),
        ("tar-dotdot", "v3.0.0", @"unexpected path or prefix \('sweep-250k/\.\./gas\.txt'\)"),
        ("tar-absolute", "v3.0.1", "unexpected path or prefix \\('" + Regex.Escape(escaped) + "'\\)"),
        ("tar-foreign-sweep", "v3.0.2", @"unexpected path or prefix \('sweep-300k/gas\.txt'\)"),
        ("tar-no-sweep-dir", "v3.0.3", @"unexpected path or prefix \('gas\.txt'\)"),
        ("tar-symlink", "v3.0.4", "link or special-file member"), ("tar-symlink-dir", "v3.0.5", "link or special-file member"),
        ("tar-hardlink", "v3.0.6", "link or special-file member"), ("tar-fifo", "v3.0.7", "link or special-file member"),
        ("tar-newline-name", "v3.0.8", "unexpected path or prefix"), ("tar-over-16mib", "v3.0.9", "expands to 1677[0-9]+ bytes, over the 16777216-byte limit"),
        ("tar-missing-gas", "v3.0.10", "has no sweep-250k/gas.txt")
    }) await Expect(name, version, 1, pattern);
    Check("no-escaped-files", !File.Exists(escaped));
    var rejected = await Expect("verifier-no-precompile", "v4.0.0", 1, @"rejected sweep-250k/verifier.hex \(precompile-push check\): no PUSH1 of 0x06 ecAdd, 0x07 ecMul, 0x08 ecPairing");
    Check("verifier-no-precompile:names-heuristic", rejected.Output.Contains("plausibility heuristic in scripts/frame-verify-gas/CheckVerifiers.cs rejected release v4.0.0"));
    foreach (var (name, version, pattern) in new[]
    {
        ("verifier-push-data-only", "v4.0.1", @"verifier.hex \(precompile-push check\)"),
        ("verifier-no-staticcall", "v4.0.2", @"verifier.hex \(staticcall check\)"),
        ("verifier-too-small", "v4.0.3", @"verifier.hex \(size check\): 107 bytes"),
        ("verifier-too-big", "v4.0.4", @"verifier.hex \(size check\): 24607 bytes"),
        ("verifier-odd-hex", "v4.0.5", @"verifier.hex \(hex check\)"),
        ("calldata-not-hex", "v4.0.6", @"calldata-invalid.hex \(hex check\)"),
        ("calldata-too-short", "v4.0.7", @"calldata-invalid.hex \(length check\)"),
        ("gas-not-integer", "v4.0.8", @"gas.txt \(integer check\)"),
        ("soispoke-no-provenance", "v5.0.0", @"provenance\.json is missing or unparsable \(FileNotFoundException\)"),
        ("soispoke-other-commit", "v5.0.1", "names upstream commit '16b88a3', not " + ArtifactFetch.SoispokeCommit)
    }) await Expect(name, version, 1, pattern);
    var redirect = "https://artifacts.invalid/v1.0.0/SHA256SUMS";
    handler.Redirects[redirect] = new Uri("http://artifacts.invalid/insecure");
    await Expect("HTTPS downgrade rejected", "v1.0.0", 1, "Only HTTPS downloads and redirects are allowed");
    handler.Redirects.Clear();
    handler.Redirects[$"https://api.github.com/repos/{ArtifactFetch.Repository}/releases/tags/v1.0.0"] = new Uri("https://release.invalid/v1.0.0");
    await Expect("cross-authority API redirect", "v1.0.0", 0, "extracted to");
    Check("API redirect drops authorization", handler.Requests[0].Authorization == "Bearer stub" && handler.Requests[1].Authorization is null);
    handler.Redirects.Clear();
    Release("v6.0.0", assets: data =>
    {
        data["sweep-250k.tar.gz"] = "not a gzip archive"u8.ToArray();
        data["SHA256SUMS"] = Encoding.ASCII.GetBytes(string.Concat(data.Where(pair => pair.Key != "SHA256SUMS").Select(pair => Convert.ToHexStringLower(SHA256.HashData(pair.Value)) + "  " + pair.Key + "\n")));
    });
    await Expect("checksummed corrupt archive", "v6.0.0", 1, "::error::sweep-250k.tar.gz is not a readable, non-empty gzip tarball");
    if (failures.Count > 0) throw new InvalidOperationException(string.Join('\n', failures));
    Console.WriteLine($"Passed {passed} tests.");
}
finally { Directory.Delete(root, recursive: true); }

void Check(string name, bool result)
{
    if (!result) { failures.Add(name); Console.Error.WriteLine("FAIL " + name); return; }
    Console.WriteLine("PASS " + name); passed++;
}

async Task<(string Run, string Output)> Expect(string name, string version, int code, string pattern, string token = "stub")
{
    var run = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(run, "temp"));
    handler.Requests.Clear();
    var console = Console.Out; using var output = new StringWriter();
    int actual;
    try { Console.SetOut(output); actual = await ArtifactFetch.Run(http, version, Path.Combine(run, "temp"), Path.Combine(run, "github_env"), token, CancellationToken.None); }
    finally { Console.SetOut(console); }
    if (Directory.GetDirectories(Path.Combine(run, "temp"), "frame-verify-gas-download.*").Length != 0 || actual != 0 && File.Exists(Path.Combine(run, "github_env")))
        throw new InvalidOperationException(name + ": failed fetch must clean its download directory and must not publish environment outputs");
    Check(name + (actual == code && Regex.IsMatch(output.ToString(), pattern) ? "" : $" (exit {actual}, expected {code} / {pattern}):\n{output}"), actual == code && Regex.IsMatch(output.ToString(), pattern));
    return (run, output.ToString());
}

void Release(string version, Action<Dictionary<string, byte[]>>? contents = null, Action<List<TarEntry>>? members = null, Action<Dictionary<string, byte[]>>? assets = null, bool draft = false, bool prerelease = false)
{
    var source = new Dictionary<string, byte[]>(files); contents?.Invoke(source);
    var data = new Dictionary<string, byte[]>();
    foreach (var label in ArtifactFetch.Labels)
    {
        using var archive = new MemoryStream();
        using (var gzip = new GZipStream(archive, CompressionMode.Compress, leaveOpen: true))
        using (var tar = new TarWriter(gzip, leaveOpen: true))
        {
            var entries = source.Where(pair => pair.Key.StartsWith("sweep-" + label + "/", StringComparison.Ordinal)).Select(pair => (TarEntry)new UstarTarEntry(TarEntryType.RegularFile, pair.Key) { DataStream = new MemoryStream(pair.Value) }).ToList();
            try
            {
                if (label == "250k") members?.Invoke(entries);
                foreach (var entry in entries) tar.WriteEntry(entry);
            }
            finally { foreach (var entry in entries) entry.DataStream?.Dispose(); }
        }
        data["sweep-" + label + ".tar.gz"] = archive.ToArray();
    }
    data["SHA256SUMS"] = Encoding.ASCII.GetBytes(string.Concat(data.Select(pair => Convert.ToHexStringLower(SHA256.HashData(pair.Value)) + "  " + pair.Key + "\n")));
    assets?.Invoke(data);
    handler.Assets[version] = data;
    handler.States[version] = (draft, prerelease);
}

static string Repeat(string text, int count) => string.Concat(Enumerable.Repeat(text, count));
static void ChangeSums(Dictionary<string, byte[]> data, Func<string[], string[]> change) => data["SHA256SUMS"] = Encoding.ASCII.GetBytes(string.Join('\n', change(Encoding.ASCII.GetString(data["SHA256SUMS"]).TrimEnd('\n').Split('\n'))) + "\n");

partial class Program
{
    [GeneratedRegex("GROTH16_ARTIFACTS_SHA256SUMS_DIGEST=[0-9a-f]{64}\n")]
    private static partial Regex ChecksumsDigest();
}

sealed class Releases : HttpMessageHandler
{
    internal Dictionary<string, Dictionary<string, byte[]>> Assets { get; } = [];
    internal Dictionary<string, (bool Draft, bool Prerelease)> States { get; } = [];
    internal List<(Uri Address, string? Authorization)> Requests { get; } = [];
    internal Dictionary<string, Uri> Redirects { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!;
        Requests.Add((url, request.Headers.Authorization?.ToString()));
        if (Redirects.TryGetValue(url.AbsoluteUri, out var redirect))
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = redirect;
            return Task.FromResult(response);
        }
        if (url.Host is "api.github.com" or "release.invalid")
        {
            var version = url.Segments[^1];
            if (!States.TryGetValue(version, out var state) || state.Draft) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var json = new JsonObject { ["draft"] = false, ["prerelease"] = state.Prerelease, ["assets"] = new JsonArray(Assets[version].Keys.Select(name => (JsonNode)new JsonObject { ["name"] = name, ["browser_download_url"] = $"https://artifacts.invalid/{version}/{name}" }).ToArray()) }.ToJsonString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
        if (url.Host == "artifacts.invalid" && Assets.TryGetValue(url.Segments[1].TrimEnd('/'), out var assets) && assets.TryGetValue(url.Segments[2], out var bytes))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        throw new InvalidOperationException("Unexpected external request: " + url);
    }
}
