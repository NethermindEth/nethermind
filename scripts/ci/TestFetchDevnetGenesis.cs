#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:include FetchDevnetGenesis.Helpers.cs

var tests = 0;
foreach (var scenario in new[]
{
    ("partial 404", 404, 200, 0), ("all missing in second repository", 200, 404, 1),
    ("unauthorized", 401, 200, 1), ("forbidden", 403, 200, 1), ("rate limited", 429, 200, 1),
    ("server failure", 500, 200, 1), ("unavailable", 503, 200, 1), ("transport failure", 0, 200, 7),
    ("commit lookup failure", 200, 200, 9), ("directory lookup failure", 200, 200, 9),
    ("no valid directories", 200, 200, 1), ("numeric sorting", 200, 200, 0)
})
{
    var (name, status, secondStatus, expectedExit) = scenario;
    var temp = Directory.CreateTempSubdirectory("devnet genesis tests ");
    try
    {
        var environmentFile = Path.Combine(temp.FullName, "github env");
        var environment = new Dictionary<string, string>
        {
            ["RUNNER_TEMP"] = temp.FullName, ["REPOS"] = "first second", ["DEVNETS_PER_REPO"] = "2",
            ["GH_TOKEN"] = "test-token", ["GITHUB_ENV"] = environmentFile
        };
        var downloads = new List<string>();
        using var output = new StringWriter();
        var result = await DevnetGenesis.Fetch(key => environment.GetValueOrDefault(key), Execute, output, CancellationToken.None);
        Assert(result == expectedExit, $"{name}: exit {result}, expected {expectedExit}");
        Assert(File.Exists(environmentFile) == (result == 0), $"{name}: environment published on failure");
        Assert(!output.ToString().Contains("test-token", StringComparison.Ordinal), "Token leaked");
        if (result == 0)
        {
            var value = File.ReadAllText(environmentFile);
            Assert(value.StartsWith("NETHERMIND_GETH_GENESIS_DIR=", StringComparison.Ordinal), "Missing variable");
            var directory = value.TrimEnd('\n').Split('=', 2)[1];
            Assert(Directory.GetFiles(directory).Length == (status == 404 ? 2 : 3), "Wrong downloaded file count");
        }
        if (name == "numeric sorting")
            Assert(downloads.SequenceEqual(new[] { "first/devnet-2", "first/devnet-10", "second/devnet-1" }), "Numeric ordering/filtering changed");
        Console.WriteLine($"PASS: {name}");
        tests++;

        Task<DevnetGenesis.CommandResult> Execute(string command, string[] arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (command == "gh")
            {
                var commit = arguments[1].Contains("/commits/", StringComparison.Ordinal);
                if ((commit && name == "commit lookup failure") || (!commit && name == "directory lookup failure"))
                    return Task.FromResult(new DevnetGenesis.CommandResult(9, ""));
                if (commit) return Task.FromResult(new DevnetGenesis.CommandResult(0, "pinned-sha\n"));
                Assert(arguments[1].EndsWith("?ref=pinned-sha", StringComparison.Ordinal), "Listing not SHA pinned");
                var listing = name == "no valid directories" ? "devnet-../bad\nother\n" :
                    arguments[1].Contains("/first/", StringComparison.Ordinal) ?
                    (name == "numeric sorting" ? "devnet-10\ndevnet-1\ndevnet-2\nother\ndevnet-../bad\n" : "devnet-1\ndevnet-2\n") : "devnet-1\n";
                return Task.FromResult(new DevnetGenesis.CommandResult(0, listing));
            }
            Assert(command == "curl", "Unexpected command");
            Assert(arguments.Contains("Authorization: Bearer test-token"), "Missing authentication");
            Assert(arguments.Contains("Accept: application/vnd.github.raw"), "Missing raw media type");
            foreach (var (flag, value) in new[] { ("--retry", "3"), ("--connect-timeout", "30"), ("--max-time", "120"), ("--write-out", "%{http_code}") })
                Assert(arguments[Array.IndexOf(arguments, flag) + 1] == value, $"Incorrect {flag}");
            var url = arguments[^1];
            Assert(url.EndsWith("?ref=pinned-sha", StringComparison.Ordinal), "Download not SHA pinned");
            var parts = new Uri(url).AbsolutePath.Split('/');
            var repo = parts[3];
            var devnet = parts[6];
            downloads.Add($"{repo}/{devnet}");
            var response = repo == "second" ? secondStatus : devnet == "devnet-1" ? 200 : status;
            File.WriteAllText(arguments[Array.IndexOf(arguments, "--output") + 1], "{}");
            return Task.FromResult(new DevnetGenesis.CommandResult(response == 0 ? 7 : 0, response.ToString()));
        }
    }
    finally { temp.Delete(recursive: true); }
}
Console.WriteLine($"Passed {tests} tests.");

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
