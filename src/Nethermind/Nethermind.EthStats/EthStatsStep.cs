// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.EthStats.Clients;
using Nethermind.EthStats.Configs;
using Nethermind.EthStats.Integrations;
using Nethermind.EthStats.Senders;
using Nethermind.Facade.Eth;
using Nethermind.Init.Steps;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.P2P;
using Nethermind.TxPool;

namespace Nethermind.EthStats;

[RunnerStepDependencies(typeof(InitializeBlockchain))]
public class EthStatsStep(
    ISpecProvider specProvider,
    ITxPool txPool,
    IBlockTree blockTree,
    IPeerManager peerManager,
    IGasPriceOracle gasPriceOracle,
    IEthSyncingInfo ethSyncingInfo,
    IEnode enode,
    IEthStatsConfig ethStatsConfig,
    INetworkConfig networkConfig,
    IInitConfig initConfig,
    IMiningConfig miningConfig,
    ILogManager logManager
) : IStep, IAsyncDisposable
{
    private readonly ILogger _logger = logManager.GetClassLogger<EthStatsStep>();

    private IEthStatsIntegration _ethStatsIntegration = null!;
    public async Task Execute(CancellationToken cancellationToken)
    {
        if (!initConfig.WebSocketsEnabled)
        {
            _logger.Warn($"{nameof(EthStatsPlugin)} disabled due to {nameof(initConfig.WebSocketsEnabled)} set to false");
        }
        else
        {
            if (_logger.IsDebug) _logger.Debug($"{nameof(EthStatsPlugin)} plugin disabled due to {nameof(EthStatsConfig)} settings set to false");
        }

        string instanceId = $"{ethStatsConfig.Name}-{Keccak.Compute(enode!.Info)}";
        if (_logger.IsInfo)
        {
            _logger.Info($"Initializing ETH Stats for the instance: {instanceId}, server: {ethStatsConfig.Server}");
        }
        MessageSender sender = new(instanceId, logManager);
        const int reconnectionInterval = 5000;
        const string api = "no";
        const string client = "0.1.1";
        const bool canUpdateHistory = false;
        string node = ProductInfo.ClientId;
        int port = networkConfig.P2PPort;
        string network = specProvider!.NetworkId.ToString();
        string protocol = $"{P2PProtocolInfoProvider.DefaultCapabilitiesToString()}";

        IEthStatsClient ethStatsClient = new EthStatsClient(
            ethStatsConfig.Server,
            reconnectionInterval,
            sender,
            logManager);

        _ethStatsIntegration = new EthStatsIntegration(
            ethStatsConfig.Name!,
            node,
            port,
            network,
            protocol,
            api,
            client,
            ethStatsConfig.Contact!,
            canUpdateHistory,
            ethStatsConfig.Secret!,
            ethStatsClient,
            sender,
            txPool!,
            blockTree!,
            peerManager!,
            gasPriceOracle!,
            ethSyncingInfo!,
            miningConfig.Enabled,
            TimeSpan.FromSeconds(ethStatsConfig.SendInterval),
            logManager);

        await _ethStatsIntegration.InitAsync();
    }

    public ValueTask DisposeAsync()
    {
        _ethStatsIntegration.Dispose();
        return ValueTask.CompletedTask;
    }
}
