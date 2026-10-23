// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

internal sealed class BenchmarkProfiling(Dictionary<string, string> state)
{
    private string Get(string key, string fallback = "") => state.GetValueOrDefault(key, BenchmarkProcess.EnvironmentValue(key, fallback));
    private string _samplingEvent = "";

    internal async Task RequirePerfAsync(CancellationToken cancellationToken)
    {
        var identity = await BenchmarkProcess.RunAsync("id", ["-u"], cancellationToken);
        if (identity.Code != 0 || identity.Output.Trim() != "0") throw new IOException("perf profiling requires the self-hosted runner to execute as root; do not wrap perf in sudo because teardown must retain the recorder PID");
        _samplingEvent = Get("PERF_SAMPLING_EVENT");
        if (_samplingEvent.Length == 0)
            foreach (var candidate in new[] { "cycles:u", "cpu-clock:u" })
                if ((await BenchmarkProcess.RunAsync("perf", ["stat", "--event", candidate, "--", "true"], cancellationToken)).Code == 0)
                {
                    _samplingEvent = candidate; break;
                }
        if (_samplingEvent.Length == 0) throw new IOException("perf can sample neither cycles:u nor cpu-clock:u as root; check kernel perf support, kernel.perf_event_paranoid, and perf_event access restrictions");
        Console.WriteLine($"perf sampling event: {_samplingEvent}");
    }

    internal static (string Start, string Name, string Executable)? Identity(string pid, string procRoot)
    {
        if (!Regex.IsMatch(pid, "^[1-9][0-9]*$")) return null;
        try
        {
            var stat = File.ReadAllText(Path.Combine(procRoot, pid, "stat"));
            var boundary = stat.LastIndexOf(") ", StringComparison.Ordinal);
            if (boundary < 0) return null;
            var fields = stat[(boundary + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length <= 19 || !Regex.IsMatch(fields[19], "^[0-9]+$")) return null;
            var name = File.ReadLines(Path.Combine(procRoot, pid, "comm")).FirstOrDefault()?.TrimEnd('\r');
            var executable = File.ResolveLinkTarget(Path.Combine(procRoot, pid, "exe"), returnFinalTarget: true)?.FullName;
            return string.IsNullOrEmpty(name) || string.IsNullOrEmpty(executable) ? null : (fields[19], name, executable);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal static async Task<bool> SignalPerfAsync(string signal, string pid, string start, string name, string executable, string procRoot, CancellationToken cancellationToken)
    {
        if (signal is not ("INT" or "KILL")) throw new ArgumentException("Unsupported recorder signal", nameof(signal));
        if (Identity(pid, procRoot) != (start, name, executable)) return false;
        return (await BenchmarkProcess.RunAsync("kill", ["-" + signal, pid], cancellationToken)).Code == 0;
    }

    internal async Task<string?> DotnetRootAsync(string container, CancellationToken cancellationToken)
    {
        var listing = await BenchmarkProcess.RunAsync("docker", ["exec", container, "dotnet", "--list-runtimes"], cancellationToken);
        foreach (var line in listing.Output.Split('\n'))
        {
            var match = Regex.Match(line.TrimEnd('\r'), @"\[([^\]]+)\]$");
            if (!match.Success) continue;
            var root = Path.GetDirectoryName(Path.GetDirectoryName(match.Groups[1].Value));
            if (root is not null && (await BenchmarkProcess.RunAsync("docker", ["exec", container, "test", "-d", root + "/host/fxr"], cancellationToken)).Code == 0) return root;
        }
        return null;
    }

    private string ContainerPid(string hostPid)
    {
        var proc = Get("RPC_BENCH_PROC_ROOT", "/proc");
        foreach (var line in File.ReadLines(Path.Combine(proc, hostPid, "status")))
            if (line.StartsWith("NSpid:", StringComparison.Ordinal))
            {
                var pid = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[^1];
                if (Regex.IsMatch(pid, "^[0-9]+$")) return pid;
            }
        throw new IOException($"could not resolve the container PID for client process {hostPid}");
    }

    private async Task<string?> FindHostPidAsync(string container, bool collector, CancellationToken cancellationToken)
    {
        var listing = await BenchmarkProcess.RunAsync("docker", ["top", container, "-eo", "pid,args"], cancellationToken);
        foreach (var line in listing.Output.Split('\n'))
        {
            var matches = collector ? line.Contains("dotnet-trace collect", StringComparison.Ordinal) : line.Contains("nethermind", StringComparison.OrdinalIgnoreCase) && !line.Contains("dottrace", StringComparison.OrdinalIgnoreCase) && !line.Contains("dotnet-trace", StringComparison.OrdinalIgnoreCase);
            if (matches) return line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        }
        return null;
    }

    private static Process StartRecorder(string executable, IEnumerable<string> arguments, string log)
    {
        // exec preserves the recorder PID while the shell supplies inherited file descriptors.
        var start = new ProcessStartInfo("sh");
        foreach (var argument in new[] { "-c", "log=$1; shift; exec \"$@\" >\"$log\" 2>&1 </dev/null", "sh", log, executable }.Concat(arguments)) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException($"Could not start {executable}");
    }

    private async Task<int> DottraceEventsAsync(string container, string kind, CancellationToken cancellationToken)
    {
        var logs = await BenchmarkProcess.RunAsync("docker", ["logs", container], cancellationToken);
        return (logs.Output + logs.Error).Split('\n').Count(line => line.Contains("##dotTrace[\"" + kind + "\"", StringComparison.Ordinal));
    }

    internal async Task StartAsync(string statePath, CancellationToken cancellationToken)
    {
        var container = Get("CONTAINER_NAME");
        if (Get("PROFILERS_STARTED_AT").Length > 0) throw new IOException($"profilers were already started for {container} at {Get("PROFILERS_STARTED_AT")} — refusing to start a second recorder");
        var diag = Get("DIAG_DIR"); var suffix = Get("INSTANCE_SUFFIX");
        if (Get("DOTTRACE", "false") == "true" && Get("DOTTRACE_DEFERRED", "false") == "true")
        {
            if (Get("DOTTRACE_STARTED_AT").Length > 0) BenchmarkProcess.Log($"dotTrace: data collection already started at {Get("DOTTRACE_STARTED_AT")}");
            else
            {
                var control = Path.Combine(diag, "dottrace", "control.svc");
                if (!File.Exists(control)) throw new IOException($"dotTrace control file {control} is missing — was the node started with deferred collection?");
                if (await DottraceEventsAsync(container, "connected", cancellationToken) == 0) throw new IOException($"dotTrace never reported ##dotTrace[\"connected\"] for {container} — the launcher is not in service-message mode");
                var before = await DottraceEventsAsync(container, "started", cancellationToken);
                File.AppendAllText(control, "\n##dotTrace[\"start\"]\r\n");
                var timeout = int.Parse(Get("DOTTRACE_START_TIMEOUT", "60"), CultureInfo.InvariantCulture);
                BenchmarkProcess.Log($"dotTrace: start message sent, waiting for ##dotTrace[\"started\"] (timeout {timeout}s)...");
                var elapsed = 0;
                while (await DottraceEventsAsync(container, "started", cancellationToken) <= before)
                {
                    await Task.Delay(1000, cancellationToken);
                    if (++elapsed >= timeout) throw new IOException($"dotTrace did not acknowledge the start message within {timeout}s — the snapshot would be empty; check the message framing in {control} and the launcher output in docker logs");
                }
                Record("DOTTRACE_STARTED_AT", DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
                BenchmarkProcess.Log("dotTrace: data collection started");
            }
        }
        if (Get("DOTNET_TRACE", "false") == "true")
        {
            if (Get("DOTNET_TRACE_PID").Length > 0) BenchmarkProcess.Log($"dotnet-trace: collector already attached (docker exec pid {Get("DOTNET_TRACE_PID")})");
            else
            {
                var node = await FindHostPidAsync(container, false, cancellationToken) ?? throw new IOException("could not find the client process for dotnet-trace");
                var client = ContainerPid(node);
                var root = await DotnetRootAsync(container, cancellationToken) ?? throw new IOException($"no .NET root with host/fxr found inside {container} via 'dotnet --list-runtimes' — the dotnet-trace apphost cannot run there");
                var log = Path.Combine(diag, "dotnet-trace", $"dotnet-trace-collect{suffix}.log");
                var arguments = new List<string> { "exec", "-e", "DOTNET_ROOT=" + root, "-e", "DOTNET_ROLL_FORWARD=Major", container, "/opt/dotnet-trace/dotnet-trace", "collect", "-p", client, "--clrevents", "gc+contention+threading+exception", "--clreventlevel", "verbose", "-o", $"/dotnet-trace-output/rpcbench{suffix}.nettrace" };
                var maximum = Get("DOTNET_TRACE_MAX_SECONDS");
                if (maximum.Length > 0)
                {
                    if (!Regex.IsMatch(maximum, "^[1-9][0-9]*$") || !long.TryParse(maximum, out var seconds)) throw new ArgumentException($"DOTNET_TRACE_MAX_SECONDS must be a positive integer (got '{maximum}')");
                    arguments.AddRange(["--duration", $"{seconds / 3600:D2}:{seconds % 3600 / 60:D2}:{seconds % 60:D2}"]);
                }
                using var process = StartRecorder("docker", arguments, log);
                await Task.Delay(1000, cancellationToken);
                if (process.HasExited) throw new IOException($"dotnet-trace did not start: {File.ReadAllText(log)}");
                var host = await FindHostPidAsync(container, true, cancellationToken) ?? throw new IOException($"dotnet-trace is running but its process is not visible in {container}");
                Record("DOTNET_TRACE_PID", process.Id.ToString(CultureInfo.InvariantCulture));
                Record("DOTNET_TRACE_COLLECTOR_PID", ContainerPid(host));
                BenchmarkProcess.Log($"dotnet-trace collecting gc+contention+threading+exception (verbose) from container pid {client}");
            }
        }
        if (Get("PERF", "false") == "true")
        {
            await RequirePerfAsync(cancellationToken);
            var node = await FindHostPidAsync(container, false, cancellationToken) ?? throw new IOException("could not find the client process for perf");
            var client = ContainerPid(node);
            var frequency = Get("PERF_FREQUENCY", "99");
            var log = Path.Combine(diag, "perf", $"perf-record{suffix}.log");
            using var recorder = StartRecorder("perf", ["record", "--event", _samplingEvent, "--freq", frequency, "--call-graph", "fp", "--pid", node, "--output", Path.Combine(diag, "perf", $"perf{suffix}.data")], log);
            await Task.Delay(1000, cancellationToken);
            if (recorder.HasExited) throw new IOException($"perf did not start: {File.ReadAllText(log)}");
            var pid = recorder.Id.ToString(CultureInfo.InvariantCulture);
            var identity = Identity(pid, Get("RPC_BENCH_PROC_ROOT", "/proc")) ?? throw new IOException("could not record perf recorder identity");
            Record("PERF_PID", pid); Record("PERF_NODE_PID", node); Record("PERF_CONTAINER_PID", client);
            Record("PERF_RECORDER_START_TIME", identity.Start); Record("PERF_RECORDER_COMM", identity.Name); Record("PERF_RECORDER_EXE", identity.Executable);
            BenchmarkProcess.Log($"perf recording pid {node} at {frequency}Hz");
        }
        Record("PROFILERS_STARTED_AT", DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        void Record(string key, string value) { BenchmarkState.Append(statePath, key, value); state[key] = value; }
    }
}
