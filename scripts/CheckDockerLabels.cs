#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:include Version.Helpers.cs
#:include ci/ScriptProcess.Helpers.cs
using System.Globalization;
using System.Text.Json.Nodes;

// Final-stage ARGs must be redeclared or OCI labels silently inherit empty values.
// Round-trip supplied build arguments through image inspection; check publishing callers separately.
// The default stub build context checks final-stage labels without compiling the client.
var full = false;
var dockerfiles = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--full": full = true; break;
        case "--dockerfile" when i + 1 < args.Length: dockerfiles.Add(args[++i]); break;
        case "-h" or "--help":
            Console.WriteLine("Usage: CheckDockerLabels.cs [--full] [--dockerfile PATH]...\nChecks published image labels and ports. Requires git and Docker with buildx.");
            return 0;
        default:
            Console.Error.WriteLine(args[i] == "--dockerfile" ? "error: --dockerfile needs a path" : $"error: unknown argument '{args[i]}'");
            return 2;
    }
}
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var tags = new List<string>();
var failureCode = 1;
DirectoryInfo? temporary = null;
try
{
    var root = ScriptVersion.Root();
    if (dockerfiles.Count == 0)
    {
        // New images must be checked or deliberately excluded, including published internal tooling.
        string[] excluded =
        [
            "Dockerfile.diag", // Ad hoc dotTrace image for gas benchmarks.
            "Dockerfile.pgo", // Throwaway collect-pgo-profile.yml image.
            "scripts/build/Dockerfile", // Release package builder; image is not pushed.
            "scripts/build/deb/Dockerfile", // Debian package builder; image is not pushed.
            "src/Nethermind/Nethermind.Runner/Dockerfile", // Visual Studio debug container.
            "src/Nethermind/Nethermind.Test.Runner/Dockerfile",
            "tools/EngineApiProxy/Dockerfile", // E2E fixture; image is never pushed.
            "tools/Kute/Dockerfile",
            "tools/RpcTests/RpcTests.Monitor/Dockerfile", // Published as nethermindeth/rpc-monitor for internal operations.
            "tools/SendBlobs/Dockerfile"
        ];
        var tracked = await Run("git", ["-C", root, "ls-files", "--", "Dockerfile", "Dockerfile.*", "*/Dockerfile", "*/Dockerfile.*"]);
        dockerfiles.AddRange(tracked.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(file => file.TrimEnd('\r')).Except(excluded).Order(StringComparer.Ordinal));
        if (dockerfiles.Count == 0) throw new InvalidOperationException("error: found no Dockerfile to check");
    }
    foreach (var builder in new[] { ".github/actions/publish-docker/action.yaml", ".github/workflows/release.yml", ".github/workflows/release-bootnode.yml" })
        foreach (var argument in new[] { "COMMIT_HASH", "VERSION", "BUILD_TIMESTAMP" })
            if (!File.ReadAllText(Path.Combine(root, builder)).Contains($"--build-arg {argument}=", StringComparison.Ordinal))
                throw new InvalidOperationException($"error: {builder} does not pass --build-arg {argument}");
    var commit = (await Run("git", ["-C", root, "rev-parse", "HEAD"])).Trim();
    var epoch = (await Run("git", ["-C", root, "log", "-1", "--format=%ct"])).Trim();
    var timestamp = DateTimeOffset.FromUnixTimeSeconds(long.Parse(epoch, CultureInfo.InvariantCulture)).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    if (commit.Length == 0) throw new InvalidOperationException("error: could not determine the commit and timestamp to build with");
    temporary = Directory.CreateTempSubdirectory("nethermind-label-check-");
    Directory.CreateDirectory(Path.Combine(temporary.FullName, "publish"));
    File.WriteAllText(Path.Combine(temporary.FullName, "publish/nethermind"), "");
    var failures = 0;
    foreach (var dockerfile in dockerfiles)
    {
        var bootnode = dockerfile == "tools/Bootnode/Dockerfile";
        if (!bootnode && dockerfile is not ("Dockerfile" or "Dockerfile.chiseled"))
        {
            Console.Error.WriteLine($"error: '{dockerfile}' has no expected labels here. Add them, or exclude it in CheckDockerLabels.cs if it is not released.");
            return 2;
        }
        var version = ScriptVersion.Read(Path.Combine(root, bootnode ? "tools/Bootnode/Nethermind.Bootnode/Nethermind.Bootnode.csproj" : "src/Nethermind/Directory.Build.props"));
        var tag = "nethermind-label-check:" + dockerfile.Replace('/', '-').Replace('.', '-');
        List<string> build = ["buildx", "build", root, "-f", Path.Combine(root, dockerfile), "-t", tag,
            "--build-arg", $"COMMIT_HASH={commit}", "--build-arg", $"VERSION={version}", "--build-arg", $"BUILD_TIMESTAMP={timestamp}", "--load"];
        build.AddRange(full ? ["--build-arg", $"SOURCE_DATE_EPOCH={epoch}"] : new[] { "--build-context", $"build={temporary.FullName}" });
        Console.WriteLine($"Building {dockerfile} as {tag}");
        tags.Add(tag);
        await Run("docker", [.. build], echo: true);
        var labels = JsonNode.Parse(await Run("docker", ["image", "inspect", "--format", "{{json .Config.Labels}}", tag]));
        Console.WriteLine($"Checking the labels of {tag}");
        foreach (var (key, expected) in new[]
        {
            ("title", bootnode ? "Nethermind Bootnode" : "Nethermind"),
            ("description", bootnode ? "Standalone discovery bootnode for discv4 and discv5." : "A robust execution client for Ethereum node operators."),
            ("vendor", "Demerzel Solutions Limited"), ("licenses", "LGPL-3.0-only"),
            ("url", bootnode ? "https://github.com/NethermindEth/nethermind/tree/master/tools/Bootnode" : "https://nethermind.io/nethermind-client"),
            ("documentation", bootnode ? "https://github.com/NethermindEth/nethermind/blob/master/tools/Bootnode/README.md" : "https://docs.nethermind.io"),
            ("source", "https://github.com/NethermindEth/nethermind"), ("version", version), ("revision", commit), ("created", timestamp)
        })
        {
            var label = $"org.opencontainers.image.{key}";
            var actual = labels?[label]?.ToString() ?? "";
            if (actual == expected) Console.WriteLine($"  ok   {label} = {actual}");
            else
            {
                Console.Error.WriteLine($"  FAIL {label}\n         expected: {expected}\n         actual:   {(actual.Length == 0 ? "<empty or missing>" : actual)}");
                failures++;
            }
        }
        if (!bootnode)
        {
            if (JsonNode.Parse(await Run("docker", ["image", "inspect", "--format", "{{json .Config.ExposedPorts}}", tag])) is not JsonObject ports ||
                !ports.ContainsKey("30303/tcp") || !ports.ContainsKey("30303/udp"))
            {
                Console.Error.WriteLine($"FAIL: {dockerfile} must expose TCP and UDP port 30303");
                failures++;
            }
        }
    }
    if (failures > 0) throw new InvalidOperationException($"{failures} image metadata check(s) failed");
    Console.WriteLine("All image labels and required exposed ports are correct");
    return 0;
}
catch (OperationCanceledException) { return 130; }
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return failureCode;
}
finally
{
    temporary?.Delete(recursive: true);
    if (tags.Count > 0)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await ScriptProcess.Run("docker", ["image", "rm", .. tags], cleanup.Token); }
        catch (Exception exception) { Console.Error.WriteLine($"Image cleanup failed: {exception.Message}"); }
    }
}

async Task<string> Run(string executable, string[] arguments, bool echo = false)
{
    var result = await ScriptProcess.Run(executable, arguments, cancellation.Token, echo);
    if (result.Code != 0)
    {
        failureCode = result.Code;
        throw new InvalidOperationException($"{executable} exited with code {result.Code}: {result.Error}");
    }
    return result.Output;
}
