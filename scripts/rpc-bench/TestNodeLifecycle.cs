#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include RpcScriptTests.Helpers.cs
#:include BenchmarkState.Helpers.cs

using System.Net;
using System.Net.Sockets;
using System.Text;

using var tests = new RpcScriptTests();
tests.Case("node state remains readable across startup and teardown", () => RpcScriptTests.Assert(BenchmarkState.Decode(BenchmarkState.Encode("/scratch/node reference")) == "/scratch/node reference", "state format changed"));
if (!OperatingSystem.IsLinux())
{
    Console.WriteLine("SKIP Linux node lifecycle command fixtures"); tests.Complete(); return;
}
var bin = Path.Combine(tests.DirectoryPath, "bin"); Directory.CreateDirectory(bin);
var database = Path.Combine(tests.DirectoryPath, "database"); Directory.CreateDirectory(database);
File.WriteAllText(Path.Combine(database, "CURRENT"), "MANIFEST-000001\n");
File.WriteAllText(Path.Combine(database, "MANIFEST-000001"), "snapshot contents\n");
var scratch = Path.Combine(tests.DirectoryPath, "scratch"); Directory.CreateDirectory(scratch);
var state = Path.Combine(tests.DirectoryPath, "state"); Directory.CreateDirectory(state);
var callLog = Path.Combine(tests.DirectoryPath, "calls");
void Executable(string name, string text)
{
    var path = Path.Combine(bin, name); File.WriteAllText(path, "#!/bin/sh\n" + text);
    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
}
Executable("docker", "printf '%s\\n' \"$*\" >> \"$FAKE_CALL_LOG\"\ncase \"$1\" in\nps) printf '%s\\n' rpcbench-primary rpcbench-reference ;;\nrun) printf '%s\\n' fake-container ;;\nlogs) printf '%s\\n' node-log-output ;;\nesac\n");
Executable("id", "printf '0\\n'\n"); Executable("mountpoint", "exit 1\n");
Executable("mount", "printf 'mount %s\\n' \"$*\" >> \"$FAKE_CALL_LOG\"\n");
Executable("umount", "printf 'umount %s\\n' \"$*\" >> \"$FAKE_CALL_LOG\"\n");
var changes = new Dictionary<string, string?> { ["PATH"] = bin + ":" + Environment.GetEnvironmentVariable("PATH"), ["DB_SOURCE"] = database, ["SCRATCH_ROOT"] = scratch, ["STATE_DIR"] = state, ["NODE_IMAGE"] = "test-image", ["FAKE_CALL_LOG"] = callLog, ["PERF"] = "false", ["DOTTRACE"] = "false", ["DOTNET_TRACE"] = "false", ["DOTNET_DUMP"] = "false", ["PROFILE_AFTER_WARMUP"] = "false", ["HEALTH_TIMEOUT"] = "5", ["INSTANCE"] = "primary", ["CLIENT"] = "nethermind", ["DB_ISOLATION"] = "copy", ["NODE_ENV_FILE"] = null, ["DIAG_DIR"] = null, ["RPC_PORT"] = null };
var previous = changes.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
try
{
    foreach (var (name, value) in changes) Environment.SetEnvironmentVariable(name, value);
    foreach (var client in new[] { "nethermind", "geth", "reth" })
        tests.Case($"{client} copy lifecycle preserves snapshot and tears down its view", () =>
        {
            Environment.SetEnvironmentVariable("CLIENT", client);
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            Environment.SetEnvironmentVariable("RPC_PORT", ((IPEndPoint)listener.LocalEndpoint).Port.ToString());
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var response = Task.Run(async () =>
            {
                using var connection = await listener.AcceptTcpClientAsync(cancellation.Token);
                await using var stream = connection.GetStream();
                using var request = new StreamReader(stream, leaveOpen: true);
                while (await request.ReadLineAsync(cancellation.Token) is { Length: > 0 }) { }
                const string body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":\"0x64\"}";
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"), cancellation.Token);
            }, cancellation.Token);
            var start = RpcScriptTests.Run("scripts/rpc-bench/StartNode.cs");
            cancellation.Cancel(); listener.Stop();
            if (start.Code == 0) response.GetAwaiter().GetResult();
            else { try { response.GetAwaiter().GetResult(); } catch (OperationCanceledException) { Console.WriteLine("Stopped failed startup fixture listener"); } }
            RpcScriptTests.Assert(start.Code == 0 && start.Output.Contains("Node ready"), start.ToString());
            var saved = BenchmarkState.Read(Path.Combine(state, "node.env"));
            RpcScriptTests.Assert(saved["CLIENT"] == client && saved["DB_SOURCE"] == database && File.Exists(Path.Combine(scratch, "run", "db", "CURRENT")), "node state/view missing");
            var stop = RpcScriptTests.Run("scripts/rpc-bench/StopNode.cs");
            RpcScriptTests.Assert(stop.Code == 0 && !Directory.Exists(Path.Combine(scratch, "run")) && File.ReadAllText(Path.Combine(database, "CURRENT")) == "MANIFEST-000001\n" && File.Exists(Path.Combine(scratch, "fingerprints", "database.txt")), stop.ToString());
            var calls = File.ReadAllText(callLog);
            RpcScriptTests.Assert(calls.Contains(client == "geth" ? ":/execution-data/geth:rw" : ":/execution-data:rw"), "client mount target changed");
        });
    tests.Case("snapshot mutation fails after cleanup", () =>
    {
        var baseline = Path.Combine(state, "db-baseline.txt");
        File.AppendAllText(baseline, "different baseline\n");
        Directory.CreateDirectory(Path.Combine(scratch, "run"));
        var stop = RpcScriptTests.Run("scripts/rpc-bench/StopNode.cs");
        RpcScriptTests.Assert(stop.Code == 1 && stop.Output.Contains("DB SNAPSHOT WAS MODIFIED") && !Directory.Exists(Path.Combine(scratch, "run")), stop.ToString());
    });
}
finally { foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value); }
tests.Complete();
