#!/usr/bin/env dotnet
#:include SyncTests.Helpers.cs
#:include ../../.github/actions/gcp-runner/Gcp.Helpers.cs
using System.Text.Json.Nodes;

if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Run process fixtures on the Linux CI runner.");
var root = Cases.Root();
var temporary = Directory.CreateTempSubdirectory("sync process fixtures ");
var saved = new Dictionary<string, string?>();
void Set(string key, string value)
{
    saved.TryAdd(key, Environment.GetEnvironmentVariable(key));
    Environment.SetEnvironmentVariable(key, value);
}
async Task<CommandResult> Script(string path) => await Gcp.Run("dotnet", ["run", "--file", Path.Combine(root, path)], null, CancellationToken.None);
void Executable(string path, string body)
{
    File.WriteAllText(path, "#!/bin/bash\n" + body + "\n");
    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
}
var cwd = Environment.CurrentDirectory;
try
{
    var bin = Path.Combine(temporary.FullName, "bin");
    Directory.CreateDirectory(bin);
    var log = Path.Combine(temporary.FullName, "node.log");
    var calls = Path.Combine(temporary.FullName, "calls");
    Set("PATH", bin + ":" + Environment.GetEnvironmentVariable("PATH"));
    Set("FIXTURE_LOG", log);
    Set("FIXTURE_CALLS", calls);
    Set("FIXTURE_STATUS", "0");
    Set("FIXTURE_STDERR", "");
    Executable(Path.Combine(bin, "docker"), "printf '%s\\n' \"$@\" > \"$FIXTURE_CALLS\"\nprintf '%s\\n' \"$FIXTURE_STDERR\" >&2\ncat \"$FIXTURE_LOG\"\nexit \"$FIXTURE_STATUS\"");
    foreach (var (mode, network, scenario) in new[] { ("Flat", "mainnet", "success"), ("HalfPath", "op-sepolia", "success"), ("Flat", "world-mainnet", "success"), ("HalfPath", "taiko-hoodi", "success"), ("HalfPath", "volta", "success"), ("Flat", "mainnet", "bad"), ("HalfPath", "mainnet", "short"), ("Flat", "mainnet", "exit"), ("invalid", "mainnet", "mode") })
        await Cases.Test($"poller {mode} {network} {scenario}", async () =>
        {
            Set("NETWORK", network);
            Set("SYNC_MODE", mode);
            Set("FIXTURE_STATUS", scenario == "exit" ? "1" : "0");
            var lines = Enumerable.Repeat("Processed Synced Chain Head", scenario == "short" ? 19 : 20).ToList();
            if (scenario == "bad") lines.Insert(0, "Exception: failed");
            if (scenario != "exit") lines.Add(mode == "Flat" ? "Verification complete. MismatchedAccounts=0, MismatchedSlots=0, MissingInFlat=0, MissingInTrie=0" : "Stats after finishing state");
            if (network == "volta") lines = lines.Select(line => line.Replace("Synced Chain Head", "", StringComparison.Ordinal)).ToList();
            File.WriteAllLines(log, lines);
            var result = await Script("scripts/WaitForSync.cs");
            Cases.Equal(scenario == "success" ? 0 : 1, result.Code);
            if (scenario == "success") Cases.True(result.Output.Contains("All required logs found.", StringComparison.Ordinal), result.Output + result.Error);
        });
    foreach (var status in new[] { "0", "1" })
        await Cases.Test($"collector docker exit {status}", async () =>
        {
            Set("NETWORK", "mainnet"); Set("SYNC_MODE", "Flat"); Set("FIXTURE_STATUS", status); Set("LOG_FILE", "");
            Set("FIXTURE_STDERR", "2026-09-16T12:01:00Z Verification complete. Accounts=2, Slots=4");
            File.WriteAllText(log, "2026-09-16T12:00:00Z Collecting trie stats\n2026-09-16T12:05:00Z Verification complete. Accounts=4, Slots=8\n");
            var output = Path.Combine(temporary.FullName, "metrics.json");
            File.Delete(output);
            var result = await Gcp.Run("dotnet", ["run", "--file", Path.Combine(root, "scripts/sync-metrics/Collect.cs"), "--", output], null, CancellationToken.None);
            Cases.Equal(int.Parse(status), result.Code);
            Cases.Equal(status == "0", File.Exists(output));
            if (status == "0") Cases.Equal(1d, JsonNode.Parse(File.ReadAllText(output))!["verify_min"]!.GetValue<double>());
        });
    var sedge = Path.Combine(temporary.FullName, "sedge");
    Directory.CreateDirectory(Path.Combine(sedge, "build"));
    Directory.CreateDirectory(Path.Combine(temporary.FullName, "tests/predefined_configs"));
    Executable(Path.Combine(sedge, "build/sedge"), "printf '%s\\n' BEGIN \"$@\" END >> \"$FIXTURE_CALLS\"\nexit \"$FIXTURE_STATUS\"");
    Environment.CurrentDirectory = sedge;
    Set("GITHUB_WORKSPACE", temporary.FullName);
    Set("DOCKER_IMAGE", "image with space");
    Set("NODE_NAME", "test node");
    Set("CL_CLIENT", "lighthouse"); Set("CL_IMAGE", "latest"); Set("CHECKPOINT_URL", "https://checkpoint.example/");
    Set("NETHERMIND_ARGS", "log=DEBUG SomeFlag=value");
    foreach (var l1 in new[] { "MAINNET", "SEPOLIA", "HOODI" }) { Set(l1 + "_EXECUTION_URL", "https://el.example/"); Set(l1 + "_CONSENSUS_URL", "https://cl.example/"); }
    foreach (var network in new[] { "mainnet", "op-mainnet", "op-sepolia", "world-mainnet", "world-sepolia", "taiko-alethia", "taiko-hoodi", "op-unknown", "taiko-unknown" })
        await Cases.Test($"sedge {network}", async () =>
        {
            Set("NETWORK", network); Set("SYNC_MODE", "Flat"); Set("FIXTURE_STATUS", "0");
    Set("FIXTURE_STDERR", "");
            File.WriteAllText(Path.Combine(temporary.FullName, "tests/predefined_configs/customNLog.config"), "config");
            File.Delete(calls);
            var result = await Script(".github/actions/sync-chain/RunSedge.cs");
            Cases.Equal(network.EndsWith("unknown", StringComparison.Ordinal) ? 1 : 0, result.Code);
            if (result.Code != 0) return;
            var arguments = File.ReadAllLines(calls);
            Cases.Equal(3, arguments.Count(argument => argument == "BEGIN"));
            Cases.Equal(network.StartsWith("world-", StringComparison.Ordinal), arguments.Contains("worldchain"));
            Cases.True(arguments.Contains("Metrics.NodeName=test node"), "argument boundaries preserved");
            Cases.True(arguments.Contains("FlatDb.Enabled=true"), "flat flag");
            Cases.True(arguments.Contains("log=DEBUG"), "extra flags");
            var index = Array.IndexOf(arguments, "--network");
            Cases.Equal(network.Contains("sepolia", StringComparison.Ordinal) ? "sepolia" : network.Contains("hoodi", StringComparison.Ordinal) ? "hoodi" : "mainnet", arguments[index + 1]);
        });
    await Cases.Test("poller cancellation terminates child", async () =>
    {
        Executable(Path.Combine(bin, "docker"), "echo $$ > \"$FIXTURE_CALLS\"\nexec sleep 60");
        Set("NETWORK", "mainnet"); Set("SYNC_MODE", "Flat");
        File.Delete(calls);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var running = Script("scripts/WaitForSync.cs");
        while (!File.Exists(calls)) await Task.Delay(50, cancellation.Token);
        // Signal the app, not the dotnet launcher, by finding the parent of the stubbed docker process.
        var childId = int.Parse(File.ReadAllText(calls).Trim());
        var status = File.ReadAllLines($"/proc/{childId}/status").First(line => line.StartsWith("PPid:", StringComparison.Ordinal));
        var parent = int.Parse(status[5..].Trim());
        var kill = await Gcp.Run("kill", ["-TERM", parent.ToString()], null, cancellation.Token);
        Cases.Equal(0, kill.Code);
        await running.WaitAsync(cancellation.Token);
        Cases.True(!Directory.Exists($"/proc/{childId}"), "docker follower reaped");
    });
}
finally
{
    Environment.CurrentDirectory = cwd;
    foreach (var (key, value) in saved) Environment.SetEnvironmentVariable(key, value);
    temporary.Delete(recursive: true);
}
Cases.Finish();
