// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Core;
using Nethermind.Init;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Runner.Ethereum.Steps;

namespace Nethermind.Runner.Ethereum;

public class EthereumRunner(INethermindApi api)
{
    private readonly INethermindApi _api = api;
    private readonly ILogger _logger = api.LogManager.GetClassLogger();

    public static readonly StepInfo[] BuiltInSteps =
    [
         typeof(InitializeStateDb),
         typeof(ApplyMemoryHint),
         typeof(DatabaseMigrations),
         typeof(EraStep),
         typeof(FilterBootnodes),
         typeof(InitCrypto),
         typeof(InitDatabase),
         typeof(InitializeBlockchain),
         typeof(InitializeBlockProducer),
         typeof(InitializeBlockTree),
         typeof(InitializeNetwork),
         typeof(InitializeNodeStats),
         typeof(InitializePlugins),
         typeof(InitializePrecompiles),
         typeof(InitTxTypesAndRlp),
         typeof(LoadGenesisBlock),
         typeof(LogHardwareInfo),
         typeof(MigrateConfigs),
         typeof(RegisterPluginRpcModules),
         typeof(RegisterRpcModules),
         typeof(ResolveIps),
         typeof(ReviewBlockTree),
         typeof(SetupKeyStore),
         typeof(StartBlockProcessor),
         typeof(StartBlockProducer),
         typeof(StartLogProducer),
         typeof(StartMonitoring),
         typeof(UpdateDiscoveryConfig),
         typeof(StartGrpc),
         typeof(StartRpc),
    ];

    public async Task Start(CancellationToken cancellationToken)
    {
        if (_logger.IsDebug) _logger.Debug("Starting Ethereum runner");

        EthereumStepsLoader stepsLoader = new(GetStepsInfo(_api));
        EthereumStepsManager stepsManager = new(stepsLoader, _api, _api.LogManager);

        await stepsManager.InitializeAll(cancellationToken);

        string infoScreen = ThisNodeInfo.BuildNodeInfoScreen();

        if (_logger.IsInfo) _logger.Info(infoScreen);
    }

    private IEnumerable<StepInfo> GetStepsInfo(INethermindApi api)
    {
        foreach (StepInfo buildInStep in BuiltInSteps)
        {
            yield return buildInStep;
        }

        foreach (INethermindPlugin plugin in _api.Plugins)
        {
            foreach (StepInfo stepInfo in plugin.GetSteps())
            {
                yield return stepInfo;
            }
        }
    }

    public async Task StopAsync()
    {
        Stop(() => _api.SessionMonitor?.Stop(), "Stopping session monitor");
        Stop(() => _api.SyncModeSelector?.Stop(), "Stopping session sync mode selector");
        Task discoveryStopTask = Stop(() => _api.DiscoveryApp?.StopAsync(), "Stopping discovery app");
        Task blockProducerTask = Stop(() => _api.BlockProducerRunner?.StopAsync(), "Stopping block producer");
        Task peerPoolTask = Stop(() => _api.PeerPool?.StopAsync(), "Stopping peer pool");
        Task peerManagerTask = Stop(() => _api.PeerManager?.StopAsync(), "Stopping peer manager");
        Task blockchainProcessorTask = Stop(() => _api.MainProcessingContext?.BlockchainProcessor?.StopAsync(), "Stopping blockchain processor");
        Task rlpxPeerTask = Stop(() => _api.RlpxPeer?.Shutdown(), "Stopping RLPx peer");
        await Task.WhenAll(discoveryStopTask, rlpxPeerTask, peerManagerTask, peerPoolTask, blockchainProcessorTask, blockProducerTask);

        foreach (INethermindPlugin plugin in _api.Plugins)
        {
            await Stop(async () => await plugin.DisposeAsync(), $"Disposing plugin {plugin.Name}");
        }

        await _api.DisposeStack.DisposeAsync();
        Stop(() => _api.DbProvider?.Dispose(), "Closing DBs");

        if (_logger.IsInfo)
        {
            _logger.Info("All DBs closed");
            _logger.Info("Ethereum runner stopped");
        }
    }

    private void Stop(Action stopAction, string description)
    {
        try
        {
            if (_logger.IsInfo) _logger.Info(description);

            stopAction();
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error($"{description} shutdown error.", e);
        }
    }

    private Task Stop(Func<Task?> stopAction, string description)
    {
        try
        {
            if (_logger.IsInfo) _logger.Info(description);
            return stopAction() ?? Task.CompletedTask;
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error($"{description} shutdown error.", e);
            return Task.CompletedTask;
        }
    }
}
