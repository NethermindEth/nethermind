#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include RpcScriptTests.Helpers.cs
#:include BenchmarkProcess.Helpers.cs
#:include BenchmarkState.Helpers.cs
#:include BenchmarkFiles.Helpers.cs
#:include BenchmarkProfiling.Helpers.cs

using System.Diagnostics;

using var tests = new RpcScriptTests();
foreach (var pid in new[] { "", "0", "-1", "abc", "../123", "123" })
    tests.Case($"missing or invalid recorder identity {pid}", () => Assert(BenchmarkProfiling.Identity(pid, tests.DirectoryPath) is null, "invalid identity accepted"));
if (!OperatingSystem.IsLinux())
{
    Console.WriteLine("SKIP Linux profiler process fixtures"); tests.Complete(); return;
}
var bin = Path.Combine(tests.DirectoryPath, "bin"); Directory.CreateDirectory(bin);
var proc = Path.Combine(tests.DirectoryPath, "proc"); Directory.CreateDirectory(proc);
var diag = Path.Combine(tests.DirectoryPath, "diag");
foreach (var directory in new[] { "perf", "dottrace", "dotnet-trace" }) Directory.CreateDirectory(Path.Combine(diag, directory));
var control = Path.Combine(diag, "dottrace/control.svc");
var calls = Path.Combine(tests.DirectoryPath, "calls");
var pids = Path.Combine(tests.DirectoryPath, "pids");
var statePath = Path.Combine(tests.DirectoryPath, "node.env");
Executable("id", "printf '%s\\n' \"${FAKE_UID:-0}\"\n");
Executable("perf", """
    printf 'perf %s\n' "$*" >> "$FAKE_CALLS"
    case "$1" in
    stat) case "$FAKE_PERF:$3" in fail:*|clock:cycles:u) exit 1;; esac ;;
    script)
      printf 'nethermind 123\n    123 [Nethermind] Namespace.Type::Method() (/tmp/perf.map)\n\n'
      test "${FAKE_PERF:-}" != script-fails ;;
    record)
      printf '%s\n' "$$" >> "$FAKE_PIDS"
      mkdir -p "$RPC_BENCH_PROC_ROOT/$$"
      printf '%s (perf) S 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 4243 0\n' "$$" > "$RPC_BENCH_PROC_ROOT/$$/stat"
      printf 'perf\n' > "$RPC_BENCH_PROC_ROOT/$$/comm"
      ln -s /bin/sh "$RPC_BENCH_PROC_ROOT/$$/exe"
      touch "$RPC_BENCH_PROC_ROOT/$$/active"
      while :; do sleep 1; done ;;
    *) exit 64 ;;
    esac
    """);
Executable("kill", """
    printf 'kill %s\n' "$*" >> "$FAKE_CALLS"
    if test "${FAKE_SIGNAL_ONLY:-false}" = true; then exit 0; fi
    case "$1" in
    -0) test -f "$RPC_BENCH_PROC_ROOT/$2/active" ;;
    -INT|-KILL) rm -f "$RPC_BENCH_PROC_ROOT/$2/active"; /bin/kill -TERM "$2" ;;
    *) exit 64 ;;
    esac
    """);
Executable("docker", """
    printf 'docker %s\n' "$*" >> "$FAKE_CALLS"
    case "$1" in
    ps) printf 'rpcbench-primary\n' ;;
    top)
      printf '%s\n' 'PID ARGS' '1200 /opt/dottrace/dottrace start --framework=NetCore' '1300 /nethermind/nethermind --datadir=/execution-data'
      if test -f "$FAKE_DIAG/collector.pid" && test "${FAKE_HIDE_COLLECTOR:-false}" != true; then
        printf '%s /opt/dotnet-trace/dotnet-trace collect -p 42\n' "$(cat "$FAKE_DIAG/collector.pid")"
      fi ;;
    logs)
      printf '##dotTrace["connected", {pid: 1300}]\n'
      if test "${FAKE_ACK:-true}" = true && grep -qF '##dotTrace["start"]' "$FAKE_DIAG/dottrace/control.svc"; then printf '##dotTrace["started", {pid: 1300}]\n'; fi ;;
    exec)
      shift
      while test "$1" = -e; do shift 2; done
      shift
      case "$*" in
      'dotnet --list-runtimes')
        if test "${FAKE_ROOTS:-one}" = multiple; then
          printf 'Microsoft.NETCore.App 8.0.0 [/opt/dotnet-a/shared/Microsoft.NETCore.App]\nMicrosoft.NETCore.App 10.0.0 [/opt/dotnet-b/shared/Microsoft.NETCore.App]\n'
        else printf 'Microsoft.NETCore.App 10.0.11 [/usr/share/dotnet/shared/Microsoft.NETCore.App]\n'; fi ;;
      'test -d '*) test "${FAKE_FXR:-true}" = true && test "$3" != /opt/dotnet-a/host/fxr ;;
      'test -s /tmp/perf-'*|'cp /tmp/perf-'*) : ;;
      '/opt/dotnet-trace/dotnet-trace collect '*)
        if test "${FAKE_TRACE_STARTS:-true}" != true; then printf '.NET location: Not found\n' >&2; exit 1; fi
        printf '%s\n' "$$" >> "$FAKE_PIDS"
        printf '%s' "$$" > "$FAKE_DIAG/collector.pid"
        mkdir -p "$RPC_BENCH_PROC_ROOT/$$"
        printf 'Name:\tdotnet-trace\nNSpid:\t%s\t77\n' "$$" > "$RPC_BENCH_PROC_ROOT/$$/status"
        touch "$RPC_BENCH_PROC_ROOT/$$/active"
        while :; do sleep 1; done ;;
      'sh -c kill -INT "$1" sh 77')
        pid=$(cat "$FAKE_DIAG/collector.pid")
        rm -f "$RPC_BENCH_PROC_ROOT/$pid/active"
        printf nettrace > "$FAKE_DIAG/dotnet-trace/rpcbench.nettrace"
        /bin/kill -TERM "$pid" ;;
      *) exit 64 ;;
      esac ;;
    stop|rm) : ;;
    *) exit 64 ;;
    esac
    """);
var changes = new Dictionary<string, string?>
{
    ["PATH"] = bin + ":" + Environment.GetEnvironmentVariable("PATH"), ["RPC_BENCH_PROC_ROOT"] = proc,
    ["FAKE_DIAG"] = diag, ["FAKE_CALLS"] = calls, ["FAKE_PIDS"] = pids, ["STATE_DIR"] = tests.DirectoryPath,
    ["NODE_ENV_FILE"] = statePath, ["PERF_SAMPLING_EVENT"] = null, ["FAKE_UID"] = null, ["FAKE_PERF"] = null,
    ["FAKE_SIGNAL_ONLY"] = null, ["FAKE_ACK"] = null, ["FAKE_FXR"] = null, ["FAKE_TRACE_STARTS"] = null, ["FAKE_ROOTS"] = null, ["FAKE_HIDE_COLLECTOR"] = null
};
var previous = changes.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
try
{
    foreach (var (key, value) in changes) Environment.SetEnvironmentVariable(key, value);
    foreach (var scenario in new[] { "cycles", "clock", "fail", "nonroot" })
        tests.Case($"perf preflight {scenario} and cached selection", () =>
        {
            Reset(); Environment.SetEnvironmentVariable("FAKE_PERF", scenario); Environment.SetEnvironmentVariable("FAKE_UID", scenario == "nonroot" ? "1000" : "0");
            var profiler = new BenchmarkProfiling([]);
            if (scenario is "fail" or "nonroot") Reject(() => profiler.RequirePerfAsync(CancellationToken.None).GetAwaiter().GetResult(), scenario == "fail" ? "neither cycles:u nor cpu-clock:u" : "execute as root");
            else { profiler.RequirePerfAsync(CancellationToken.None).GetAwaiter().GetResult(); profiler.RequirePerfAsync(CancellationToken.None).GetAwaiter().GetResult(); }
            var commands = Commands().Where(line => line.StartsWith("perf ", StringComparison.Ordinal)).ToArray();
            Assert(commands.Length == (scenario == "nonroot" ? 0 : scenario == "cycles" ? 1 : 2), string.Join('\n', commands));
            if (commands.Length > 0) Assert(commands[0] == "perf stat --event cycles:u -- true", "cycles probe changed");
            if (commands.Length > 1) Assert(commands[1] == "perf stat --event cpu-clock:u -- true", "clock fallback changed");
        });
    tests.Case("recorder identity protects against PID reuse", () =>
    {
        Reset(); var directory = Path.Combine(proc, "123"); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "stat"), "123 (perf with spaces) S " + string.Join(' ', Enumerable.Repeat("0", 18)) + " 4243 0\n");
        File.WriteAllText(Path.Combine(directory, "comm"), "perf\n"); File.CreateSymbolicLink(Path.Combine(directory, "exe"), "/bin/sh");
        var identity = BenchmarkProfiling.Identity("123", proc) ?? throw new Exception("missing valid identity");
        Assert(identity.Start == "4243" && identity.Name == "perf", "identity fields changed");
        Assert(!BenchmarkProfiling.SignalPerfAsync("INT", "123", "4242", identity.Name, identity.Executable, proc, CancellationToken.None).GetAwaiter().GetResult(), "reused PID accepted");
        Assert(Commands().Length == 0, "reused PID was signaled");
        Environment.SetEnvironmentVariable("FAKE_SIGNAL_ONLY", "true");
        Assert(BenchmarkProfiling.SignalPerfAsync("INT", "123", identity.Start, identity.Name, identity.Executable, proc, CancellationToken.None).GetAwaiter().GetResult(), "matching recorder refused");
        Assert(Commands().SequenceEqual(["kill -INT 123"]), "matching recorder signal changed");
    });
    foreach (var scenario in new[] { "plain", "trace", "trace-fails", "no-fxr", "no-ack", "retry" })
        tests.Case($"profiler startup lifecycle {scenario}", () =>
        {
            Reset(); var state = State(); var tracing = scenario is "trace" or "trace-fails" or "no-fxr";
            state["DOTNET_TRACE"] = tracing ? "true" : "false";
            if (scenario == "retry") state["DOTTRACE_STARTED_AT"] = "2026-08-26T00:00:00Z";
            foreach (var (key, value) in state) BenchmarkState.Append(statePath, key, value);
            Environment.SetEnvironmentVariable("FAKE_TRACE_STARTS", scenario == "trace-fails" ? "false" : "true");
            Environment.SetEnvironmentVariable("FAKE_FXR", scenario == "no-fxr" ? "false" : "true");
            Environment.SetEnvironmentVariable("FAKE_ACK", scenario is "no-ack" or "retry" ? "false" : "true");
            var profiler = new BenchmarkProfiling(state);
            if (scenario is "trace-fails" or "no-fxr" or "no-ack")
            {
                Reject(() => profiler.StartAsync(statePath, CancellationToken.None).GetAwaiter().GetResult(), scenario switch { "trace-fails" => ".NET location: Not found", "no-fxr" => "no .NET root with host/fxr", _ => "did not acknowledge" });
                Assert(!Commands().Any(line => line.StartsWith("perf record", StringComparison.Ordinal)), "perf started after collector failure");
                Assert(!BenchmarkState.Read(statePath).ContainsKey("PROFILERS_STARTED_AT"), "failed start marked complete");
                Assert(!BenchmarkState.Read(statePath).ContainsKey("DOTNET_TRACE_PID"), "failed collector PID persisted");
                return;
            }
            profiler.StartAsync(statePath, CancellationToken.None).GetAwaiter().GetResult();
            Reject(() => profiler.StartAsync(statePath, CancellationToken.None).GetAwaiter().GetResult(), "refusing to start a second recorder");
            var saved = BenchmarkState.Read(statePath);
            Assert(saved["PERF_NODE_PID"] == "1300" && saved["PERF_CONTAINER_PID"] == "42" && saved["PERF_RECORDER_START_TIME"] == "4243" && saved["PERF_RECORDER_COMM"] == "perf", "recorder identity not persisted");
            Assert(File.ReadAllText(control) == (scenario == "retry" ? "" : "\n##dotTrace[\"start\"]\r\n"), "dotTrace framing or retry changed");
            Assert(File.ReadLines(statePath).Count(line => line.StartsWith("DOTTRACE_STARTED_AT=", StringComparison.Ordinal)) == 1, "duplicate dotTrace start");
            var records = Commands().Where(line => line.StartsWith("perf record", StringComparison.Ordinal)).ToArray();
            Assert(records.Length == 1 && records[0].Contains("--event cycles:u --freq 99 --call-graph fp --pid 1300"), "recorder arguments changed");
            if (!tracing) { Assert(!saved.ContainsKey("DOTNET_TRACE_PID") && !Commands().Any(line => line.StartsWith("docker exec", StringComparison.Ordinal)), "EventPipe attached without opt-in"); return; }
            Assert(saved["DOTNET_TRACE_COLLECTOR_PID"] == "77", "wrong collector PID");
            var text = File.ReadAllText(statePath); Assert(text.IndexOf("DOTNET_TRACE_PID=", StringComparison.Ordinal) < text.IndexOf("PERF_PID=", StringComparison.Ordinal), "perf started before EventPipe");
            var collect = Commands().Where(line => line.Contains(" collect ", StringComparison.Ordinal)).ToArray();
            Assert(collect.Length == 1 && collect[0] == "docker exec -e DOTNET_ROOT=/usr/share/dotnet -e DOTNET_ROLL_FORWARD=Major rpcbench-primary /opt/dotnet-trace/dotnet-trace collect -p 42 --clrevents gc+contention+threading+exception --clreventlevel verbose -o /dotnet-trace-output/rpcbench.nettrace --duration 01:05:00", "collector launch changed");
            BenchmarkState.Append(statePath, "PERF", "false");
            var stop = RpcScriptTests.Run("scripts/rpc-bench/StopNode.cs");
            Assert(stop.Code == 0, stop.ToString());
            Assert(File.ReadAllText(Path.Combine(diag, "dotnet-trace/rpcbench.nettrace")) == "nettrace", "trace not flushed");
            var commands = Commands(); Assert(Array.FindIndex(commands, line => line.Contains("kill -INT", StringComparison.Ordinal)) < Array.FindIndex(commands, line => line.StartsWith("docker stop", StringComparison.Ordinal)), "node stopped before collector");
        });
    foreach (var trace in new[] { false, true }) tests.Case($"cancelled startup cleans unpersisted recorder EventPipe={trace}", () =>
    {
        Reset(); var state = State(); state["DOTTRACE"] = "false"; state["DOTNET_TRACE"] = trace ? "true" : "false"; state["PERF"] = trace ? "false" : "true";
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var startup = new BenchmarkProfiling(state).StartAsync(statePath, cancellation.Token);
        var deadline = Stopwatch.StartNew();
        while (new FileInfo(pids).Length == 0 && deadline.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(10);
        Assert(new FileInfo(pids).Length > 0, "fixture recorder did not launch");
        cancellation.Cancel();
        try { startup.GetAwaiter().GetResult(); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { }
        foreach (var pid in File.ReadAllLines(pids))
        {
            try { using var process = Process.GetProcessById(int.Parse(pid)); Assert(process.HasExited, "cancelled recorder survived startup"); }
            catch (ArgumentException) { Console.WriteLine($"Cancelled recorder {pid} reaped"); }
        }
        Assert(!BenchmarkState.Read(statePath).ContainsKey(trace ? "DOTNET_TRACE_PID" : "PERF_PID"), "cancelled recorder persisted");
    });
    tests.Case("runtime probing preserves later candidate roots", () =>
    {
        Reset(); Environment.SetEnvironmentVariable("FAKE_ROOTS", "multiple");
        Assert(new BenchmarkProfiling([]).DotnetRootAsync("rpcbench-primary", CancellationToken.None).GetAwaiter().GetResult() == "/opt/dotnet-b", "later runtime lost");
        Assert(Commands().Count(line => line.Contains("test -d", StringComparison.Ordinal)) == 2, "root probing skipped candidate");
    });
    foreach (var alive in new[] { false, true }) tests.Case($"collector shutdown refuses stale identity alive={alive}", () =>
    {
        Reset(); var state = State(); state["PERF"] = "false"; state["DOTNET_TRACE"] = "true"; state["DOTNET_TRACE_PID"] = "98765"; state["DOTNET_TRACE_COLLECTOR_PID"] = "77";
        if (alive) { Directory.CreateDirectory(Path.Combine(proc, "98765")); File.WriteAllText(Path.Combine(proc, "98765/active"), ""); }
        foreach (var (key, value) in state) BenchmarkState.Append(statePath, key, value);
        File.WriteAllLines(Path.Combine(diag, "dotnet-trace/dotnet-trace-collect.log"), Enumerable.Range(0, 25).Select(index => $"collector line {index}"));
        var stop = RpcScriptTests.Run("scripts/rpc-bench/StopNode.cs");
        Assert(stop.Code == 1 && stop.Output.Contains(alive ? "refusing to signal" : "exited before it was stopped", StringComparison.Ordinal), stop.ToString());
        Assert(stop.Output.Contains("::error::") && stop.Error.Contains("collector line 5\n") && stop.Error.Contains("collector line 24") && !stop.Error.Contains("collector line 4\n"), "collector diagnostic tail changed");
        Assert(!Commands().Any(line => line.Contains("kill -INT", StringComparison.Ordinal)), "unknown collector was signaled");
    });
    foreach (var fail in new[] { false, true }) tests.Case($"perf folding streams stdout and checks exit fail={fail}", () =>
    {
        Reset(); var state = State(); state["DOTTRACE"] = "false";
        foreach (var (key, value) in state) BenchmarkState.Append(statePath, key, value);
        new BenchmarkProfiling(state).StartAsync(statePath, CancellationToken.None).GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(diag, "perf/perf.data"), "data");
        var folded = Path.Combine(diag, "perf/perf.folded"); File.Delete(folded);
        Environment.SetEnvironmentVariable("FAKE_PERF", fail ? "script-fails" : "");
        var stop = RpcScriptTests.Run("scripts/rpc-bench/StopNode.cs");
        Assert(stop.Code == (fail ? 1 : 0), stop.ToString());
        Assert(File.Exists(folded) != fail && !File.Exists(folded + ".tmp") && !File.Exists(folded + ".script.tmp"), "partial or raw profile retained");
        if (!fail) Assert(File.ReadAllText(folded).Contains("Namespace.Type::Method()"), "managed stack lost");
    });
}
finally
{
    CleanupProcesses();
    foreach (var (key, value) in previous) Environment.SetEnvironmentVariable(key, value);
}
tests.Complete();

void Executable(string name, string body)
{
    var path = Path.Combine(bin, name); File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
}
void Reset()
{
    CleanupProcesses(); File.WriteAllText(calls, ""); File.WriteAllText(pids, ""); File.WriteAllText(control, ""); File.WriteAllText(statePath, ""); File.Delete(Path.Combine(diag, "collector.pid"));
    foreach (var key in changes.Keys.Where(key => key.StartsWith("FAKE_", StringComparison.Ordinal) && key is not ("FAKE_DIAG" or "FAKE_CALLS" or "FAKE_PIDS"))) Environment.SetEnvironmentVariable(key, null);
}
void CleanupProcesses()
{
    if (!File.Exists(pids)) return;
    foreach (var pid in File.ReadAllLines(pids))
        try { using var process = Process.GetProcessById(int.Parse(pid)); if (!process.HasExited) process.Kill(entireProcessTree: true); process.WaitForExit(); }
        catch (ArgumentException) { Console.WriteLine($"Fixture process {pid} already exited"); }
}
Dictionary<string, string> State()
{
    Directory.CreateDirectory(Path.Combine(proc, "1300")); File.WriteAllText(Path.Combine(proc, "1300/status"), "Name:\tnethermind\nNSpid:\t1300\t42\n");
    var run = Path.Combine(tests.DirectoryPath, "run"); Directory.CreateDirectory(run);
    var database = Path.Combine(tests.DirectoryPath, "database"); Directory.CreateDirectory(database);
    BenchmarkFiles.FingerprintAsync(database, Path.Combine(tests.DirectoryPath, "db-baseline.txt"), CancellationToken.None).GetAwaiter().GetResult();
    return new Dictionary<string, string>
    {
        ["CLIENT"] = "nethermind", ["CONTAINER_NAME"] = "rpcbench-primary", ["DIAG_DIR"] = diag, ["INSTANCE_SUFFIX"] = "",
        ["DOTTRACE"] = "true", ["DOTTRACE_DEFERRED"] = "true", ["PERF"] = "true", ["PERF_FREQUENCY"] = "99", ["PROFILE_AFTER_WARMUP"] = "true",
        ["DOTTRACE_START_TIMEOUT"] = "1", ["DOTNET_TRACE_STOP_TIMEOUT"] = "3", ["DOTNET_TRACE_MAX_SECONDS"] = "3900", ["RPC_BENCH_PROC_ROOT"] = proc,
        ["DB_SOURCE"] = database, ["DB_ISOLATION"] = "copy", ["RUN_SCRATCH"] = run, ["DOTNET_DUMP"] = "false"
    };
}
string[] Commands() => File.Exists(calls) ? File.ReadAllLines(calls) : [];
static void Assert(bool condition, string message) => RpcScriptTests.Assert(condition, message);
static void Reject(Action action, string expected)
{
    try { action(); }
    catch (IOException error) { Assert(error.Message.Contains(expected, StringComparison.Ordinal), error.Message); return; }
    throw new Exception("Expected failure: " + expected);
}
