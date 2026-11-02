#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include script-process.helpers.cs

if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Genesis tool fixtures require Linux Bash.");
var root = new DirectoryInfo(Environment.CurrentDirectory);
while (root is not null && !File.Exists(Path.Combine(root.FullName, "scripts/ci/fetch-devnet-genesis.cs"))) root = root.Parent;
var temp = Directory.CreateTempSubdirectory("devnet genesis tests ");
var environment = new Dictionary<string, string>
{
    ["PATH"] = temp.FullName + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
    ["REPOS"] = "first second", ["DEVNETS_PER_REPO"] = "2", ["GH_TOKEN"] = "test-token"
};
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
try
{
    var build = await ScriptProcess.Run("dotnet", ["build", Path.Combine(root!.FullName, "scripts/ci/fetch-devnet-genesis.cs"), "--output", temp.FullName], timeout.Token);
    Assert(build.Code == 0, build.Output + build.Error);
    foreach (var (key, value) in environment) Environment.SetEnvironmentVariable(key, value);
    foreach (var command in new[] { "gh", "curl" })
    {
        var stub = Path.Combine(temp.FullName, command);
        File.WriteAllText(stub, """
            #!/bin/bash
            set -euo pipefail
            if [[ ${0##*/} == gh ]]; then
              [[ $# == 4 && $1 == api && $3 == --jq ]] || exit 97
              if [[ $2 == */commits/HEAD ]]; then
                [[ $SCENARIO != "commit lookup failure" ]] || exit 9
                printf 'pinned-sha\n'
              else
                [[ $SCENARIO != "directory lookup failure" ]] || exit 9
                [[ $2 == *"?ref=pinned-sha" ]] || exit 97
                if [[ $SCENARIO == "no valid directories" ]]; then printf 'devnet-../bad\nother\n'
                elif [[ $2 == */first/* && $SCENARIO == "numeric sorting" ]]; then printf 'devnet-10\ndevnet-1\ndevnet-2\nother\ndevnet-../bad\n'
                elif [[ $2 == */first/* ]]; then printf 'devnet-1\ndevnet-2\n'
                else printf 'devnet-1\n'; fi
              fi
              exit
            fi
            [[ $# == 17 && $1 == --silent && $2 == --show-error && $3 == --retry && $4 == 3 &&
               $5 == --connect-timeout && $6 == 30 && $7 == --max-time && $8 == 120 &&
               $9 == -H && ${10} == "Authorization: Bearer test-token" &&
               ${11} == -H && ${12} == "Accept: application/vnd.github.raw" &&
               ${13} == --output && ${15} == --write-out && ${16} == '%{http_code}' ]] || exit 97
            url=${17}
            [[ $url == *"?ref=pinned-sha" ]] || exit 97
            repo=${url#https://api.github.com/repos/ethpandaops/}; repo=${repo%%/*}
            devnet=${url#*/network-configs/}; devnet=${devnet%%/*}
            printf '%s/%s\n' "$repo" "$devnet" >> "$DOWNLOAD_LOG"
            response=$STATUS
            if [[ $repo == second ]]; then response=$SECOND_STATUS
            elif [[ $devnet == devnet-1 ]]; then response=200; fi
            printf '{}' > "${14}"
            [[ $response != 0 ]] || exit 7
            printf '%s' "$response"
            """ + "\n");
        File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    var tests = 0;
    foreach (var (name, status, secondStatus, expectedExit) in new[]
    {
        ("partial 404", 404, 200, 0), ("all missing in second repository", 200, 404, 1),
        ("unauthorized", 401, 200, 1), ("forbidden", 403, 200, 1), ("rate limited", 429, 200, 1),
        ("server failure", 500, 200, 1), ("unavailable", 503, 200, 1), ("transport failure", 0, 200, 7),
        ("commit lookup failure", 200, 200, 9), ("directory lookup failure", 200, 200, 9),
        ("no valid directories", 200, 200, 1), ("numeric sorting", 200, 200, 0)
    })
    {
        var directory = Directory.CreateDirectory(Path.Combine(temp.FullName, name)).FullName;
        var environmentFile = Path.Combine(directory, "github env");
        var downloads = Path.Combine(directory, "downloads");
        foreach (var (key, value) in new[]
        {
            ("RUNNER_TEMP", directory), ("GITHUB_ENV", environmentFile), ("DOWNLOAD_LOG", downloads),
            ("SCENARIO", name), ("STATUS", status.ToString()), ("SECOND_STATUS", secondStatus.ToString())
        }) Environment.SetEnvironmentVariable(key, value);
        var result = await ScriptProcess.Run("dotnet", [Path.Combine(temp.FullName, "fetch-devnet-genesis.dll")], timeout.Token);
        Assert(result.Code == expectedExit, $"{name}: exit {result.Code}, expected {expectedExit}\n{result.Output}\n{result.Error}");
        Assert(File.Exists(environmentFile) == (result.Code == 0), $"{name}: environment published on failure");
        Assert(!(result.Output + result.Error).Contains("test-token", StringComparison.Ordinal), "Token leaked");
        if (result.Code == 0)
        {
            var value = File.ReadAllText(environmentFile);
            Assert(value.StartsWith("NETHERMIND_GETH_GENESIS_DIR=", StringComparison.Ordinal), "Missing variable");
            var downloaded = value.TrimEnd('\n').Split('=', 2)[1];
            Assert(Directory.GetFiles(downloaded).Length == (status == 404 ? 2 : 3), "Wrong downloaded file count");
        }
        if (name == "numeric sorting")
            Assert(File.ReadAllLines(downloads).SequenceEqual(new[] { "first/devnet-2", "first/devnet-10", "second/devnet-1" }), "Numeric ordering/filtering changed");
        Console.WriteLine($"PASS: {name}");
        tests++;
    }
    Console.WriteLine($"Passed {tests} tests.");
}
finally { temp.Delete(recursive: true); }

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
