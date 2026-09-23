// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Init.Steps;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Taiko.Config;
using Nethermind.Taiko.Precompiles;
using Nethermind.Taiko.TaikoSpec;

namespace Nethermind.Taiko;

/// <summary>
/// Wires the Taiko L1 precompiles (L1SLOAD / L1STATICCALL) by pointing their static providers
/// at an L1 JSON-RPC client, replacing the plugin's former <c>Init(INethermindApi)</c> hook.
/// </summary>
/// <remarks>
/// The precompile providers are stored in static fields that the EVM reads during transaction/block
/// execution, so they must be configured before the earliest possible EVM execution. That earliest
/// execution is genesis, run by <see cref="LoadGenesisBlock"/>; hence it is the sole declared dependent.
/// <see cref="InitializeNetwork"/> (sync execution) already depends on <see cref="LoadGenesisBlock"/>,
/// so ordering this step before genesis transitively covers it. <see cref="InitializeBlockchain"/> only
/// builds the processing pipeline without executing any block, and <see cref="LoadGenesisBlock"/> already
/// depends on it, so no direct ordering constraint is needed. <c>RegisterRpcModules</c> merely registers
/// modules; any RPC-triggered execution occurs strictly after genesis load.
/// </remarks>
[RunnerStepDependencies(
    dependencies: [typeof(InitializeBlockTree)],
    dependents: [typeof(LoadGenesisBlock)]
)]
public class InitializeTaikoPlugin(
    ISpecProvider specProvider,
    ILogManager logManager,
    ISurgeConfig surgeConfig,
    IJsonSerializer jsonSerializer,
    IDisposableStack disposeStack) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(specProvider);

        if (specProvider.GetFinalSpec() is not TaikoReleaseSpec taikoSpec)
            throw new InvalidOperationException("TaikoPlugin requires TaikoChainSpecBasedSpecProvider");

        ILogger logger = logManager.GetClassLogger<TaikoPlugin>();

        bool sloadEnabled = taikoSpec.IsRip7728Enabled;
        bool staticCallEnabled = taikoSpec.IsL1StaticCallEnabled;

        if (logger.IsInfo) logger.Info($"L1SLOAD (RIP-7728): {(sloadEnabled ? "enabled" : "disabled")}");
        if (logger.IsInfo) logger.Info($"L1STATICCALL: {(staticCallEnabled ? "enabled" : "disabled")}");

        if (!sloadEnabled && !staticCallEnabled)
            return Task.CompletedTask;

        if (string.IsNullOrEmpty(surgeConfig.L1EthApiEndpoint))
            throw new ArgumentException($"{nameof(surgeConfig.L1EthApiEndpoint)} must be provided in the Surge configuration to use L1 precompiles");

        if (logger.IsInfo) logger.Info($"L1 precompiles: using L1 endpoint: {surgeConfig.L1EthApiEndpoint}");

        // Single RPC client shared by both L1 precompile providers. Process-lifetime scope.
        IJsonRpcClient l1RpcClient = new BasicJsonRpcClient(
            new Uri(surgeConfig.L1EthApiEndpoint),
            jsonSerializer,
            logManager,
            L1PrecompileConstants.L1RpcTimeout);
        disposeStack.Push((IDisposable)l1RpcClient);

        if (sloadEnabled)
        {
            L1SloadPrecompile.L1StorageProvider = new JsonRpcL1StorageProvider(l1RpcClient, logManager);
            L1SloadPrecompile.Logger = logManager.GetClassLogger<L1SloadPrecompile>();
            if (logger.IsInfo) logger.Info("L1SLOAD: precompile initialized");
        }

        if (staticCallEnabled)
        {
            L1StaticCallPrecompile.L1CallProvider = new JsonRpcL1CallProvider(l1RpcClient, logManager);
            L1StaticCallPrecompile.Logger = logManager.GetClassLogger<L1StaticCallPrecompile>();
            if (logger.IsInfo) logger.Info("L1STATICCALL: precompile initialized");
        }

        return Task.CompletedTask;
    }
}
