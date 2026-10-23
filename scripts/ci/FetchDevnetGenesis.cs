#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include ScriptProcess.Helpers.cs
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

#if !SCRIPT_TEST
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    return await DevnetGenesis.Fetch(Environment.GetEnvironmentVariable, DevnetGenesis.Execute, Console.Out, cancellation.Token);
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    return 130;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
#endif

static partial class DevnetGenesis
{
    [GeneratedRegex("^devnet-[0-9]+$")]
    private static partial Regex DevnetName();

    internal static async Task<int> Fetch(Func<string, string?> environment,
        Func<string, string[], CancellationToken, Task<(int ExitCode, string Output)>> execute,
        TextWriter output, CancellationToken cancellationToken)
    {
        var temp = Required("RUNNER_TEMP");
        var repos = Required("REPOS").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var count = int.Parse(Required("DEVNETS_PER_REPO"), CultureInfo.InvariantCulture);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var token = Required("GH_TOKEN");
        var environmentFile = Required("GITHUB_ENV");
        var directory = Path.Combine(temp, "geth-genesis." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        foreach (var repo in repos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var commit = await execute("gh", ["api", $"repos/ethpandaops/{repo}/commits/HEAD", "--jq", ".sha"], cancellationToken);
            if (commit.ExitCode != 0) return commit.ExitCode;
            var sha = commit.Output.TrimEnd('\r', '\n');
            var listing = await execute("gh", ["api", $"repos/ethpandaops/{repo}/contents/network-configs?ref={sha}", "--jq", ".[] | select(.type == \"dir\") | .name"], cancellationToken);
            if (listing.ExitCode != 0) return listing.ExitCode;
            var devnets = listing.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(name => DevnetName().IsMatch(name))
                .OrderBy(name => BigInteger.Parse(name.AsSpan(7), CultureInfo.InvariantCulture))
                .ThenBy(name => name, StringComparer.Ordinal)
                .TakeLast(count).ToArray();
            if (devnets.Length == 0)
            {
                output.WriteLine($"::error::ethpandaops/{repo}@{sha} has no network-configs/devnet-<n> directories");
                return 1;
            }
            var fetched = 0;
            foreach (var devnet in devnets)
            {
                var path = $"network-configs/{devnet}/metadata/genesis.json";
                var file = Path.Combine(directory, $"{repo}-{devnet}.json");
                var result = await execute("curl", ["--silent", "--show-error", "--retry", "3", "--connect-timeout", "30", "--max-time", "120",
                    "-H", $"Authorization: Bearer {token}", "-H", "Accept: application/vnd.github.raw",
                    "--output", file, "--write-out", "%{http_code}",
                    $"https://api.github.com/repos/ethpandaops/{repo}/contents/{path}?ref={sha}"], cancellationToken);
                if (result.ExitCode != 0) return result.ExitCode;
                switch (result.Output)
                {
                    case "200":
                        output.WriteLine($"Fetched ethpandaops/{repo}@{sha} {path}");
                        fetched++;
                        break;
                    case "404":
                        output.WriteLine($"::warning::ethpandaops/{repo}@{sha} has no {path}");
                        File.Delete(file);
                        break;
                    default:
                        output.WriteLine($"::error::Fetching ethpandaops/{repo}@{sha} {path} returned HTTP {result.Output}");
                        return 1;
                }
            }
            if (fetched == 0)
            {
                output.WriteLine($"::error::No genesis files fetched from ethpandaops/{repo}@{sha}");
                return 1;
            }
        }
        await File.AppendAllTextAsync(environmentFile, $"NETHERMIND_GETH_GENESIS_DIR={directory}\n", new UTF8Encoding(false), cancellationToken);
        return 0;

        string Required(string name) => environment(name) ?? throw new InvalidOperationException($"{name} is required.");
    }

    internal static async Task<(int ExitCode, string Output)> Execute(string command, string[] arguments, CancellationToken cancellationToken)
    {
        var result = await ScriptProcess.Run(command, arguments, cancellationToken);
        Console.Error.Write(result.Error);
        return (result.Code, result.Output);
    }
}
