// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Config;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Init.Steps;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Json;

namespace Nethermind.Merge.Plugin;

/// <summary>
/// Runs the merge plugin's one-off initialization: registers the engine-API JSON resolver, reconciles the
/// slot-time config, validates merge settings and ensures an engine JSON-RPC URL is configured.
/// </summary>
/// <remarks>
/// <see cref="InitializeBlockchain"/> and <see cref="RegisterRpcModules"/> are declared as dependents so they run
/// after the slot-time reconciliation and the engine JSON resolver / <see cref="IJsonRpcConfig"/> mutations they
/// rely on.
/// </remarks>
[RunnerStepDependencies(dependencies: [typeof(InitializeBlockTree)], dependents: [typeof(InitializeBlockchain), typeof(RegisterRpcModules)])]
public class InitializeMergePlugin(
    IMergeConfig mergeConfig,
    IBlocksConfig blocksConfig,
    ISpecProvider specProvider,
    IJsonRpcConfig jsonRpcConfig,
    ILogManager logManager) : IStep
{
    private readonly ILogger _logger = logManager.GetClassLogger<InitializeMergePlugin>();

    public Task Execute(CancellationToken cancellationToken)
    {
        EthereumJsonSerializer.AddTypeInfoResolver(EngineApiJsonContext.Default, JsonTypeInfoResolverPriority.EngineApi);

        MergePlugin.MigrateSecondsPerSlot(blocksConfig, mergeConfig);

        EnsureNotConflictingSettings();
        EnsureJsonRpcUrl();

        return Task.CompletedTask;
    }

    private void EnsureNotConflictingSettings()
    {
        if (!mergeConfig.Enabled && mergeConfig.TerminalTotalDifficulty is not null)
        {
            throw new InvalidConfigurationException(
                $"{nameof(MergeConfig)}.{nameof(MergeConfig.TerminalTotalDifficulty)} cannot be set when {nameof(MergeConfig)}.{nameof(MergeConfig.Enabled)} is false.",
                ExitCodes.ConflictingConfigurations);
        }
    }

    private void EnsureJsonRpcUrl()
    {
        if (!HasTtd()) // by default we have Merge.Enabled = true, for chains that are not post-merge, we can skip this check, but we can still working with MergePlugin
            return;

        if (!jsonRpcConfig.Enabled)
        {
            if (_logger.IsInfo)
                _logger.Info("JsonRpc not enabled. Turning on JsonRpc URL with engine API.");

            jsonRpcConfig.Enabled = true;

            EnsureEngineModuleIsConfigured();

            if (!jsonRpcConfig.EnabledModules.Contains(ModuleType.Engine, StringComparison.OrdinalIgnoreCase))
            {
                // Disable it
                jsonRpcConfig.EnabledModules = [];
            }

            jsonRpcConfig.AdditionalRpcUrls = jsonRpcConfig.AdditionalRpcUrls
                .Where(static (url) => JsonRpcUrl.Parse(url).EnabledModules.Contains(ModuleType.Engine, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        else
        {
            EnsureEngineModuleIsConfigured();
        }
    }

    private void EnsureEngineModuleIsConfigured()
    {
        JsonRpcUrlCollection urlCollection = new(logManager, jsonRpcConfig, false);
        bool hasEngineApiConfigured = urlCollection
            .Values
            .Any(static rpcUrl => rpcUrl.EnabledModules.Contains(ModuleType.Engine, StringComparison.OrdinalIgnoreCase));

        if (!hasEngineApiConfigured)
        {
            throw new InvalidConfigurationException(
                "Engine module wasn't configured on any port. Nethermind can't work without engine port configured. Verify your RPC configuration. You can find examples in our docs: https://docs.nethermind.io/interacting/json-rpc-server/#engine-api",
                ExitCodes.NoEngineModule);
        }
    }

    private bool HasTtd() => specProvider.TerminalTotalDifficulty is not null || mergeConfig.TerminalTotalDifficulty is not null;
}
