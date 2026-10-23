#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:include ../../../scripts/sync/Sync.Helpers.cs

try
{
    string Env(string key) => Environment.GetEnvironmentVariable(key) ?? "";
    var mode = Env("SYNC_MODE");
    if (!new[] { "flat", "halfpath" }.Contains(mode.ToLowerInvariant())) { Console.WriteLine($"Unsupported sync mode: {mode}"); return 1; }
    var flat = mode.Equals("flat", StringComparison.OrdinalIgnoreCase) ? "true" : "false";
    using var cancellation = new CancellationTokenSource();
    using var signal = OperatingSystem.IsWindows() ? null : System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, context => { context.Cancel = true; cancellation.Cancel(); });
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    async Task<int> Sedge(IEnumerable<string> arguments)
    {
        using var process = Sync.Start("./build/sedge", arguments);
        try { await process.WaitForExitAsync(cancellation.Token); return process.ExitCode; }
        finally { Sync.Stop(process); }
    }
    if (await Sedge(["deps", "install"]) is var install && install != 0) return install;
    var network = Env("NETWORK");
    var op = network.StartsWith("op-", StringComparison.Ordinal) || network.StartsWith("world-", StringComparison.Ordinal);
    var taiko = network.StartsWith("taiko-", StringComparison.Ordinal);
    var flag = op ? "--el-op-extra-flag" : taiko ? "--el-l2-extra-flag" : "--el-extra-flag";
    var data = op ? "execution-data-op" : taiko ? "execution-data-taiko" : "execution-data";
    var arguments = new List<string> { "generate", "--logging", "none", "-p", Path.Combine(Env("GITHUB_WORKSPACE"), "sedge") };
    if (op || taiko)
    {
        var l1 = op ? network.Contains("mainnet", StringComparison.Ordinal) ? "mainnet" : network.Contains("sepolia", StringComparison.Ordinal) ? "sepolia" : ""
            : network.Contains("alethia", StringComparison.Ordinal) ? "mainnet" : network.Contains("hoodi", StringComparison.Ordinal) ? "hoodi" : "";
        if (l1.Length == 0) { Console.WriteLine($"Unknown network: {network}"); return 1; }
        arguments.AddRange(op ? ["op-full-node", "--op-execution", $"opnethermind:{Env("DOCKER_IMAGE")}", "--op-image", "op-node:us-docker.pkg.dev/oplabs-tools-artifacts/images/op-node:latest"]
            : ["taiko-full-node", "--l2-execution", $"taiko-nethermind:{Env("DOCKER_IMAGE")}", "--taiko-image", "taiko:us-docker.pkg.dev/evmchain/images/taiko-client:latest"]);
        arguments.AddRange(["--map-all", "--network", op ? Sync.L1(network) : l1, "--consensus-url", Env($"{l1.ToUpperInvariant()}_CONSENSUS_URL"), "--execution-api-url", Env($"{l1.ToUpperInvariant()}_EXECUTION_URL")]);
        if (op) Console.WriteLine($"network={network} resolved to L1 network={Sync.L1(network)}");
    }
    else arguments.AddRange(["full-node", "-c", $"{Env("CL_CLIENT")}:{Env("CL_IMAGE")}", "-e", $"nethermind:{Env("DOCKER_IMAGE")}", "--map-all", "--no-mev-boost", "--no-validator", "--network", network]);
    var configs = Path.Combine(data, "logs", "configs");
    Directory.CreateDirectory(configs);
    File.Move("../tests/predefined_configs/customNLog.config", Path.Combine(configs, "customNLog.config"), overwrite: true);
    void Extra(string value) => arguments.AddRange([flag, value]);
    foreach (var value in new[] { $"FlatDb.Enabled={flat}", "Sync.NonValidatorNode=true", "Sync.DownloadBodiesInFastSync=false", "Sync.DownloadReceiptsInFastSync=false" }) Extra(value);
    if (!op && !taiko) Extra("JsonRpc.EnabledModules=[Eth,Subscribe,Trace,TxPool,Web3,Personal,Proof,Net,Parity,Health,Rpc,Debug]");
    if (op || taiko) Extra("loggerConfigSource=/nethermind/data/logs/configs/customNLog.config");
    Extra("Sync.VerifyTrieOnStateSyncFinished=true");
    if (!op && !taiko)
    {
        Extra("loggerConfigSource=/nethermind/data/logs/configs/customNLog.config");
        Extra("Sync.SnapSync=true");
    }
    foreach (var value in new[] { $"Metrics.NodeName={Env("NODE_NAME")}", "Metrics.Enabled=true", "Init.LogRules=Synchronization.Peers.SyncPeersReport:Debug" }) Extra(value);
    if (network.StartsWith("world-", StringComparison.Ordinal)) arguments.AddRange(["--chain", "worldchain"]);
    foreach (var value in Env("NETHERMIND_ARGS").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) Extra(value);
    if (!op && !taiko) arguments.Add($"--checkpoint-sync-url={Env("CHECKPOINT_URL")}");
    var generate = await Sedge(arguments);
    return generate != 0 ? generate : await Sedge(["run", "-p", Path.Combine(Env("GITHUB_WORKSPACE"), "sedge")]);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 143;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
