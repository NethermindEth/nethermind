#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include script-process.helpers.cs
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
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
        cancellation.Token.ThrowIfCancellationRequested();
        var commit = await Execute("gh", ["api", $"repos/ethpandaops/{repo}/commits/HEAD", "--jq", ".sha"], cancellation.Token);
        if (commit.ExitCode != 0) return commit.ExitCode;
        var sha = commit.Output.TrimEnd('\r', '\n');
        var listing = await Execute("gh", ["api", $"repos/ethpandaops/{repo}/contents/network-configs?ref={sha}", "--jq", ".[] | select(.type == \"dir\") | .name"], cancellation.Token);
        if (listing.ExitCode != 0) return listing.ExitCode;
        var devnets = listing.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(name => DevnetName().IsMatch(name))
            .OrderBy(name => BigInteger.Parse(name.AsSpan(7), CultureInfo.InvariantCulture))
            .ThenBy(name => name, StringComparer.Ordinal)
            .TakeLast(count).ToArray();
        if (devnets.Length == 0)
        {
            Console.WriteLine($"::error::ethpandaops/{repo}@{sha} has no network-configs/devnet-<n> directories");
            return 1;
        }
        var fetched = 0;
        foreach (var devnet in devnets)
        {
            var path = $"network-configs/{devnet}/metadata/genesis.json";
            var file = Path.Combine(directory, $"{repo}-{devnet}.json");
            var result = await Execute("curl", ["--silent", "--show-error", "--retry", "3", "--connect-timeout", "30", "--max-time", "120",
                "-H", $"Authorization: Bearer {token}", "-H", "Accept: application/vnd.github.raw",
                "--output", file, "--write-out", "%{http_code}",
                $"https://api.github.com/repos/ethpandaops/{repo}/contents/{path}?ref={sha}"], cancellation.Token);
            if (result.ExitCode != 0) return result.ExitCode;
            switch (result.Output)
            {
                case "200":
                    Console.WriteLine($"Fetched ethpandaops/{repo}@{sha} {path}");
                    fetched++;
                    break;
                case "404":
                    Console.WriteLine($"::warning::ethpandaops/{repo}@{sha} has no {path}");
                    File.Delete(file);
                    break;
                default:
                    Console.WriteLine($"::error::Fetching ethpandaops/{repo}@{sha} {path} returned HTTP {result.Output}");
                    return 1;
            }
        }
        if (fetched == 0)
        {
            Console.WriteLine($"::error::No genesis files fetched from ethpandaops/{repo}@{sha}");
            return 1;
        }
    }
    await File.AppendAllTextAsync(environmentFile, $"NETHERMIND_GETH_GENESIS_DIR={directory}\n", new UTF8Encoding(false), cancellation.Token);
    return 0;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return 130; }
catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }

static string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} is required.");

static async Task<(int ExitCode, string Output)> Execute(string command, string[] arguments, CancellationToken cancellationToken)
{
    var result = await ScriptProcess.Run(command, arguments, cancellationToken);
    Console.Error.Write(result.Error);
    return (result.Code, result.Output);
}

partial class Program
{
    [GeneratedRegex("^devnet-[0-9]+$")]
    private static partial Regex DevnetName();
}
