// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
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

    public static readonly StepInfo[] BuildInSteps =
    [
         new(typeof(InitializeStateDb)),
         new(typeof(ApplyMemoryHint)),
         new(typeof(DatabaseMigrations)),
         new(typeof(EraStep)),
         new(typeof(FilterBootnodes)),
         new(typeof(InitCrypto)),
         new(typeof(InitDatabase)),
         new(typeof(InitializeBlockchain)),
         new(typeof(InitializeBlockProducer)),
         new(typeof(InitializeBlockTree)),
         new(typeof(InitializeNetwork)),
         new(typeof(InitializeNodeStats)),
         new(typeof(InitializePlugins)),
         new(typeof(InitializePrecompiles)),
         new(typeof(InitTxTypesAndRlp)),
         new(typeof(LoadGenesisBlock)),
         new(typeof(LogHardwareInfo)),
         new(typeof(MigrateConfigs)),
         new(typeof(RegisterPluginRpcModules)),
         new(typeof(RegisterRpcModules)),
         new(typeof(ResolveIps)),
         new(typeof(ReviewBlockTree)),
         new(typeof(SetupKeyStore)),
         new(typeof(StartBlockProcessor)),
         new(typeof(StartBlockProducer)),
         new(typeof(StartLogProducer)),
         new(typeof(StartMonitoring)),
         new(typeof(UpdateDiscoveryConfig)),
         new(typeof(StartGrpc)),
         new(typeof(StartRpc)),
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
        foreach (StepInfo buildInStep in BuildInSteps)
        {
            yield return buildInStep;
        }

        IEnumerable<IInitializationPlugin> enabledInitializationPlugins = _api.Plugins.OfType<IInitializationPlugin>();

        foreach (IInitializationPlugin initializationPlugin in enabledInitializationPlugins)
        {
            foreach (StepInfo stepInfo in EthereumStepsLoader.LoadStepInfoFromAssembly(initializationPlugin.GetType().Assembly))
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
        Task blockchainProcessorTask = Stop(() => _api.BlockchainProcessor?.StopAsync(), "Stopping blockchain processor");
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
