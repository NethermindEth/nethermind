#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:include ../../ci/script-process.helpers.cs

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Quorum node tests require Linux.");
if (args.Length != 3) throw new ArgumentException("Expected upstream checkout, Cargo build log, and candidate image.");
using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(15));
var cancellationToken = lifetime.Token;
var upstream = Path.GetFullPath(args[0]);
var buildLog = Path.GetFullPath(args[1]);
var output = Path.GetFullPath("artifacts/eez-quorum");
Directory.CreateDirectory(output);
var candidate = await Run("docker", ["image", "inspect", args[2]], cancellationToken);
using var images = JsonDocument.Parse(candidate);
var image = images.RootElement[0];
var imageId = image.GetProperty("Id").GetString()!;
var revision = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? throw new InvalidOperationException("Missing candidate revision.");
if (!Checks.ImageId().IsMatch(imageId) || image.GetProperty("Config").GetProperty("Labels")
        .GetProperty("org.opencontainers.image.revision").GetString() != revision)
    throw new InvalidOperationException("Candidate image provenance mismatch.");
var binaries = new HashSet<string>(StringComparer.Ordinal);
foreach (var line in File.ReadLines(buildLog))
{
    using var document = JsonDocument.Parse(line);
    var entry = document.RootElement;
    if (entry.GetProperty("reason").GetString() == "compiler-artifact" &&
        entry.GetProperty("target").GetProperty("name").GetString() == "e2e" &&
        entry.GetProperty("profile").GetProperty("test").GetBoolean() &&
        entry.GetProperty("executable").ValueKind == JsonValueKind.String)
        binaries.Add(entry.GetProperty("executable").GetString()!);
}
if (binaries.Count != 1) throw new InvalidOperationException("Expected one compiled upstream e2e executable.");
var binary = binaries.Single();
var nativeSigner = Path.Combine(upstream, "target/ci-e2e/eez-proof-signer");
var composer = Path.Combine(upstream, "target/ci-e2e/eez-composer");
var cases = new[]
{
    "happy_case_quorum_two_of_two",
    "quorum_two_of_three_settles_with_an_unresponsive_attester",
    "quorum_settles_without_an_attester_signing_the_wrong_digest",
    "quorum_prover_rejects_a_foreign_rollup_identity"
};
var inventory = await Run(binary, ["--list", "--format", "terse", "--color", "never"], cancellationToken, upstream);
var available = inventory.Split('\n').ToHashSet(StringComparer.Ordinal);
foreach (var name in cases)
    if (!available.Contains(name + ": test"))
        throw new InvalidOperationException($"Missing upstream test: {name}");
var source = (await Run("git", ["rev-parse", "HEAD"], cancellationToken, upstream)).Trim();
if (source != Environment.GetEnvironmentVariable("EEZ_QUORUM_REVISION"))
    throw new InvalidOperationException("Upstream source differs from the selected revision.");
var protocol = (await Run("git", ["-C", "eez-core-protocol", "rev-parse", "HEAD"], cancellationToken, upstream)).Trim();
var identity = new JsonObject
{
    ["upstream"] = source,
    ["protocol"] = protocol,
    ["candidate"] = revision,
    ["image"] = imageId,
    ["testBinary"] = Hash(binary),
    ["composer"] = Hash(composer),
    ["nativeSigner"] = Hash(nativeSigner)
};
await File.WriteAllTextAsync(Path.Combine(output, "identity.json"), identity.ToJsonString());
var failures = 0;
foreach (var name in cases)
{
    var directory = Path.Combine(output, name);
    Directory.CreateDirectory(directory);
    var container = "eez-quorum-" + Guid.NewGuid().ToString("N");
    var launcher = Path.Combine(directory, "eez-proof-signer");
    File.Copy("scripts/eez/devnet/quorum-prover.sh", launcher);
    File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    await File.WriteAllTextAsync(Path.Combine(directory, "config.json"), new JsonObject
    {
        ["image"] = imageId,
        ["nativeSigner"] = nativeSigner,
        ["container"] = container
    }.ToJsonString());
    Environment.SetEnvironmentVariable("EEZ_TEST_PROOF_SIGNER_BIN", launcher);
    Environment.SetEnvironmentVariable("EEZ_TEST_LOG_DIR", Path.Combine(directory, "logs"));
    var started = Stopwatch.StartNew();
    var passed = false;
    try
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var result = await ScriptProcess.Run(binary, ["--exact", name, "--test-threads=1", "--color", "never"], timeout.Token,
            echo: true, workingDirectory: upstream);
        await File.WriteAllTextAsync(Path.Combine(directory, "test.log"), result.Output + result.Error);
        if (result.Code != 0 || !Checks.OnePassed().IsMatch(result.Output))
            throw new InvalidOperationException($"Upstream test did not pass exactly once: {name}");
        using var participant = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "participant.json")));
        if (participant.RootElement.GetProperty("image").GetString() != imageId)
            throw new InvalidOperationException("Test did not use the selected candidate.");
        using var running = JsonDocument.Parse(await Run("docker", ["container", "inspect", container], cancellationToken));
        if (running.RootElement[0].GetProperty("Image").GetString() != imageId)
            throw new InvalidOperationException("Running prover image differs from the candidate.");
        passed = true;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"{name}: {error}");
        failures++;
    }
    finally
    {
        try
        {
            using var diagnostics = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var logs = await ScriptProcess.Run("docker", ["logs", container], diagnostics.Token);
            await File.WriteAllTextAsync(Path.Combine(directory, "prover.log"), logs.Output + logs.Error);
            var proxy = await ScriptProcess.Run("docker", ["exec", container, "cat", "/data/keystore/streaming/haproxy.log"], diagnostics.Token);
            await File.WriteAllTextAsync(Path.Combine(directory, "proxy.log"), proxy.Output + proxy.Error);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var removal = await ScriptProcess.Run("docker", ["rm", "-f", container], cleanup.Token);
            if (removal.Code != 0 && !removal.Error.Contains("No such container", StringComparison.Ordinal))
                throw new InvalidOperationException($"Could not remove owned test prover: {removal.Error}");
            await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), new JsonObject
            {
                ["test"] = name,
                ["passed"] = passed,
                ["elapsedSeconds"] = started.Elapsed.TotalSeconds,
                ["identity"] = identity.DeepClone()
            }.ToJsonString());
        }
    }
}
Console.WriteLine($"Quorum interoperability: {cases.Length - failures}/{cases.Length} passed.");
return failures == 0 ? 0 : 1;

static async Task<string> Run(string executable, string[] arguments, CancellationToken cancellationToken, string? directory = null)
{
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(30));
    var result = await ScriptProcess.Run(executable, arguments, timeout.Token, workingDirectory: directory);
    if (result.Code != 0) throw new InvalidOperationException($"{executable} failed: {result.Error}");
    return result.Output;
}

static string Hash(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexStringLower(SHA256.HashData(stream));
}

static partial class Checks
{
    [GeneratedRegex("^sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant, 1000)]
    internal static partial Regex ImageId();

    [GeneratedRegex(@"test result: ok\. 1 passed; 0 failed; 0 ignored;", RegexOptions.CultureInvariant, 1000)]
    internal static partial Regex OnePassed();
}
