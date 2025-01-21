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
         new StepInfo(typeof(InitializeStateDb)),
         new StepInfo(typeof(ApplyMemoryHint)),
         new StepInfo(typeof(DatabaseMigrations)),
         new StepInfo(typeof(EraStep)),
         new StepInfo(typeof(FilterBootnodes)),
         new StepInfo(typeof(InitCrypto)),
         new StepInfo(typeof(InitDatabase)),
         new StepInfo(typeof(InitializeBlockchain)),
         new StepInfo(typeof(InitializeBlockProducer)),
         new StepInfo(typeof(InitializeBlockTree)),
         new StepInfo(typeof(InitializeNetwork)),
         new StepInfo(typeof(InitializeNodeStats)),
         new StepInfo(typeof(InitializePlugins)),
         new StepInfo(typeof(InitializePrecompiles)),
         new StepInfo(typeof(InitTxTypesAndRlp)),
         new StepInfo(typeof(LoadGenesisBlock)),
         new StepInfo(typeof(LogHardwareInfo)),
         new StepInfo(typeof(MigrateConfigs)),
         new StepInfo(typeof(RegisterPluginRpcModules)),
         new StepInfo(typeof(RegisterRpcModules)),
         new StepInfo(typeof(ResolveIps)),
         new StepInfo(typeof(ReviewBlockTree)),
         new StepInfo(typeof(SetupKeyStore)),
         new StepInfo(typeof(StartBlockProcessor)),
         new StepInfo(typeof(StartBlockProducer)),
         new StepInfo(typeof(StartLogProducer)),
         new StepInfo(typeof(StartMonitoring)),
         new StepInfo(typeof(UpdateDiscoveryConfig)),
         new StepInfo(typeof(StartGrpc)),
         new StepInfo(typeof(StartRpc)),
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
