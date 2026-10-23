#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include RpcScriptTests.Helpers.cs

using var tests = new RpcScriptTests();
foreach (var script in new[] { "RunFlood.cs", "RunEthCallChaos.cs" })
    tests.Case(script + " rejects missing output", () =>
    {
        var previous = Environment.GetEnvironmentVariable("OUT_DIR");
        try
        {
            Environment.SetEnvironmentVariable("OUT_DIR", null);
            var result = RpcScriptTests.Run("scripts/rpc-bench/" + script);
            RpcScriptTests.Assert(result.Code == 1 && result.Error.Contains("OUT_DIR", StringComparison.Ordinal), result.ToString());
        }
        finally { Environment.SetEnvironmentVariable("OUT_DIR", previous); }
    });
if (!OperatingSystem.IsLinux()) { Console.WriteLine("SKIP Linux load generator fixtures"); tests.Complete(); return; }
var bin = Path.Combine(tests.DirectoryPath, "bin"); Directory.CreateDirectory(bin);
Directory.CreateDirectory(Path.Combine(tests.DirectoryPath, ".local", "share"));
void Executable(string name, string body)
{
    var path = Path.Combine(bin, name); File.WriteAllText(path, "#!/bin/bash\nset -eu\n" + body);
    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
}
Executable("id", "echo 0\n"); Executable("vegeta", "exit 0\n");
Executable("git", "if [[ $1 == init ]]; then mkdir -p \"${@: -1}/src/EthCallChaos\"; fi\n");
Executable("uv", "if [[ $1 == tool && $2 == dir ]]; then echo \"$FAKE_BIN\"; fi\n");
Executable("docker", """
    printf '%s\n' "$*" >> "$COMMAND_LOG"
    if [[ "$1" == logs ]]; then echo fixture-container-log; fi
    """);
Executable("curl", """
    if [[ "${ECC_FAIL_START:-}" == true ]]; then exit 1; fi
    destination=''; previous=''
    for argument in "$@"; do [[ "$previous" != -o ]] || destination="$argument"; previous="$argument"; done
    if [[ -n "$destination" ]]; then
      if [[ "$destination" == *leaderboard* ]]; then printf '[{"rankPosition":1,"meanMs":2,"p99Ms":3,"toAddress":"fixture","calldataPreview":"0x"}]' > "$destination"
      else printf '{"total":2}' > "$destination"; fi
    else echo '{}'; fi
    """);
Executable("flood", """
    echo fixture-load-output
    output=''; previous=''
    for argument in "$@"; do [[ "$previous" != --output ]] || output="$argument"; previous="$argument"; done
    if [[ "${FLOOD_MISSING:-}" != true ]]; then
      mkdir -p "$output"
      printf '{"results":{"nethermind":{"target_rate":[10],"actual_rate":[9.5],"success":[1],"mean":[0.001],"p50":[0.001],"p90":[0.002],"p99":[0.003],"max":[0.004],"requests":[10]}}}' > "$output/results.json"
    fi
    exit 7
    """);
var changes = new Dictionary<string, string?>
{
    ["HOME"] = tests.DirectoryPath, ["DOTNET_CLI_HOME"] = Environment.GetEnvironmentVariable("HOME"), ["PATH"] = bin + ":" + Environment.GetEnvironmentVariable("PATH"), ["FAKE_BIN"] = bin,
    ["OUT_DIR"] = null, ["SCRATCH_ROOT"] = Path.Combine(tests.DirectoryPath, "scratch"), ["COMMAND_LOG"] = Path.Combine(tests.DirectoryPath, "commands"),
    ["ECC_CORPUS_DB"] = tests.Write("corpus.db", "fixture corpus"), ["ECC_DURATION"] = "0", ["ECC_FAIL_START"] = null, ["FLOOD_MISSING"] = null,
    ["TESTS"] = "fixture", ["REFERENCE_RPC_URL"] = null
};
var previousValues = changes.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
try
{
    foreach (var (name, value) in changes) Environment.SetEnvironmentVariable(name, value);
    foreach (var missing in new[] { false, true })
        tests.Case("flood gates artifact completeness after nonzero tool exit " + missing, () =>
        {
            var output = Path.Combine(tests.DirectoryPath, "flood-" + missing); Environment.SetEnvironmentVariable("OUT_DIR", output); Environment.SetEnvironmentVariable("FLOOD_MISSING", missing ? "true" : "false");
            var result = RpcScriptTests.Run("scripts/rpc-bench/RunFlood.cs");
            RpcScriptTests.Assert(result.Code == (missing ? 1 : 0) && result.Output.Contains("fixture-load-output", StringComparison.Ordinal), result.ToString());
            RpcScriptTests.Assert(File.Exists(Path.Combine(output, "fixture.log")) && File.Exists(Path.Combine(output, "flood-summary.md")), "flood artifacts lost");
        });
    foreach (var failed in new[] { false, true })
        tests.Case("EthCallChaos persists output and tears down startup failure " + failed, () =>
        {
            var output = Path.Combine(tests.DirectoryPath, "chaos-" + failed); Environment.SetEnvironmentVariable("OUT_DIR", output); Environment.SetEnvironmentVariable("ECC_FAIL_START", failed ? "true" : "false");
            var result = RpcScriptTests.Run("scripts/rpc-bench/RunEthCallChaos.cs");
            RpcScriptTests.Assert(result.Code == (failed ? 1 : 0), result.ToString());
            RpcScriptTests.Assert(File.ReadAllText(Path.Combine(output, "ethcallchaos.log")).Contains("fixture-container-log", StringComparison.Ordinal), "container diagnostics lost");
            RpcScriptTests.Assert(File.ReadLines(changes["COMMAND_LOG"]!).Last() == "rm -f ethcallchaos-bench", "owned container not removed");
            if (!failed) RpcScriptTests.Assert(File.ReadAllText(Path.Combine(output, "ethcallchaos-summary.md")).Contains("| 1 | 2 | 3 | fixture | 0x |", StringComparison.Ordinal) && File.ReadAllText(Path.Combine(output, "ethcallchaos.db")) == "fixture corpus", "scraped results or evolved corpus lost");
        });
}
finally { foreach (var (name, value) in previousValues) Environment.SetEnvironmentVariable(name, value); }
tests.Complete();
