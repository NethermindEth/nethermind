// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Text.RegularExpressions;

internal static partial class ReceiptMigrationGate
{
    internal static async Task WaitAsync(
        Func<CancellationToken, Task<(bool Running, string Logs)>> snapshot,
        string logPath,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        TimeSpan? pollInterval = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var elapsed = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                var (running, logs) = await snapshot(deadline.Token);
                await File.WriteAllTextAsync(logPath, logs, deadline.Token);
                if (!running) throw new IOException("node exited while waiting for receipt tx-hash migration");
                var clean = AnsiRegex().Replace(logs, "");
                if (clean.Contains("Adding transaction hashes to stored receipts failed", StringComparison.Ordinal))
                    throw new IOException("receipt tx-hash migration failed; refusing to time requests");
                var started = StartedRegex().Match(clean);
                var completed = CompletedRegex().Match(clean);
                if (started.Success && completed.Success && completed.Index > started.Index)
                {
                    BenchmarkProcess.Log($"Receipt tx-hash migration completed before timing ({elapsed.Elapsed}).");
                    return;
                }
                await Task.Delay(pollInterval ?? TimeSpan.FromSeconds(10), deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new IOException($"receipt tx-hash migration did not positively complete within {timeout}; refusing to time requests");
        }
    }

    internal static async Task<(bool Running, string Logs)> SnapshotAsync(string container, CancellationToken cancellationToken)
    {
        var running = await BenchmarkProcess.RunAsync("docker", ["inspect", "--format", "{{.State.Running}}", container], cancellationToken);
        var logs = await BenchmarkProcess.RunAsync("docker", ["logs", container], cancellationToken);
        if (running.Code != 0 || logs.Code != 0) throw new IOException("cannot inspect receipt migration readiness");
        return (running.Output.Trim() == "true", logs.Output + logs.Error);
    }

    [GeneratedRegex("\\x1B\\[[0-9;]*m")]
    private static partial Regex AnsiRegex();
    [GeneratedRegex(@"Adding transaction hashes to stored receipts, blocks [0-9]+ to [0-9]+\.")]
    private static partial Regex StartedRegex();
    [GeneratedRegex(@"Transaction hashes added to stored receipts of [0-9]+ blocks, [0-9]+ bytes added\.")]
    private static partial Regex CompletedRegex();
}

internal static class NodeIoDiagnostics
{
    internal static async Task CaptureIdentityAsync(string rpc, string image, string flags, string output, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var chain = await Call("eth_chainId", "[]");
        var head = await Call("eth_getBlockByNumber", "[\"latest\",false]");
        var version = await Call("web3_clientVersion", "[]");
        if (chain is null || head?["number"] is null || head["hash"] is null || version is null)
            throw new IOException("missing runtime identity before timing");
        var identity = new System.Text.Json.Nodes.JsonObject
        {
            ["observed_utc"] = DateTime.UtcNow.ToString("O"), ["image"] = image, ["flags"] = flags,
            ["chainId"] = chain.DeepClone(), ["head"] = head["number"]!.DeepClone(),
            ["hash"] = head["hash"]!.DeepClone(), ["clientVersion"] = version.DeepClone()
        };
        await File.WriteAllTextAsync(output, identity.ToJsonString(), cancellationToken);

        async Task<System.Text.Json.Nodes.JsonNode?> Call(string method, string parameters)
        {
            using var body = new StringContent($"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"{method}\",\"params\":{parameters}}}", System.Text.Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(rpc, body, cancellationToken);
            response.EnsureSuccessStatusCode();
            var value = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (value?["error"] is not null) throw new IOException("runtime identity RPC failed: " + method);
            return value?["result"];
        }
    }

    internal static async Task CaptureAsync(string container, string output, CancellationToken cancellationToken)
    {
        await using var writer = File.CreateText(output);
        await writer.WriteLineAsync($"Read-only diagnostics at {DateTime.UtcNow:O}; absence/failure does not establish backend equality.");
        await Capture("uname", ["-a"]);
        await Capture("docker", ["info", "--format", "{{json .SecurityOptions}}"]);
        await Capture("docker", ["inspect", "--format", "image={{.Image}} pid={{.State.Pid}} security={{json .HostConfig.SecurityOpt}} privileged={{.HostConfig.Privileged}} capadd={{json .HostConfig.CapAdd}} capdrop={{json .HostConfig.CapDrop}} apparmor={{.AppArmorProfile}} cpuset={{.HostConfig.CpusetCpus}} memory={{.HostConfig.Memory}}", container]);
        await Capture("docker", ["exec", container, "sh", "-c", "grep -E '^(Name|Seccomp|Seccomp_filters|NoNewPrivs|CapEff|CapBnd):' /proc/1/status; for p in /proc/sys/kernel/io_uring_disabled /proc/sys/kernel/io_uring_group; do if test -r \"$p\"; then printf '%s=' \"$p\"; cat \"$p\"; fi; done; find /nethermind -maxdepth 2 -name '*rocksdb*.so' -exec sha256sum '{}' ';'; find /execution-data -maxdepth 6 -name 'OPTIONS-*' -type f -exec grep -HE '^[[:space:]]*(read_io_executor_threads|use_direct_reads|use_direct_io_for_flush_and_compaction|allow_mmap_reads|allow_mmap_writes|async_io)[[:space:]]*=' '{}' ';'"]);

        async Task Capture(string command, string[] arguments)
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(TimeSpan.FromSeconds(30));
            await writer.WriteLineAsync($"$ {command} {string.Join(' ', arguments)}");
            try
            {
                var result = await BenchmarkProcess.RunAsync(command, arguments, bounded.Token);
                await writer.WriteLineAsync($"exit={result.Code}\n{result.Output}{result.Error}");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await writer.WriteLineAsync("unavailable: diagnostic command timed out");
            }
            catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception)
            {
                await writer.WriteLineAsync("unavailable: " + exception.Message);
            }
        }
    }
}
