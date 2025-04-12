// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.EthStats.Clients;
using Nethermind.EthStats.Configs;
using Nethermind.EthStats.Integrations;
using Nethermind.EthStats.Senders;
using Nethermind.Network.Config;
using Nethermind.Network.P2P;

namespace Nethermind.EthStats;

public class EthStatsPlugin(IEthStatsConfig ethStatsConfig) : INethermindPlugin
{
    private IEthStatsClient _ethStatsClient = null!;
    private IEthStatsIntegration _ethStatsIntegration = null!;
    private INethermindApi _api = null!;
    private Logging.ILogger _logger;

    private readonly bool _isOn = ethStatsConfig.Enabled;

    public bool Enabled => _isOn;

    public string Name => "EthStats";
    public string Description => "Ethereum Statistics";
    public string Author => "Nethermind";

    public ValueTask DisposeAsync()
    {
        _ethStatsIntegration?.Dispose();
        return ValueTask.CompletedTask;
    }

    public Task Init(INethermindApi nethermindApi)
    {
        _api = nethermindApi;
        var (getFromAPi, _) = _api.ForInit;

        IInitConfig initConfig = getFromAPi.Config<IInitConfig>();
        _logger = getFromAPi.LogManager.GetClassLogger();

        if (!_isOn)
        {

            if (!initConfig.WebSocketsEnabled)
            {
                _logger.Warn($"{nameof(EthStatsPlugin)} disabled due to {nameof(initConfig.WebSocketsEnabled)} set to false");
            }
            else
            {
                if (_logger.IsDebug) _logger.Debug($"{nameof(EthStatsPlugin)} plugin disabled due to {nameof(EthStatsConfig)} settings set to false");
            }
        }

        return Task.CompletedTask;
    }

    public async Task InitNetworkProtocol()
    {
        (IApiWithNetwork getFromAPi, _) = _api.ForNetwork;
        INetworkConfig networkConfig = _api.Config<INetworkConfig>();
        IInitConfig initConfig = _api.Config<IInitConfig>();

        if (_isOn)
        {
            string instanceId = $"{ethStatsConfig.Name}-{Keccak.Compute(getFromAPi.Enode!.Info)}";
            if (_logger.IsInfo)
            {
                _logger.Info($"Initializing ETH Stats for the instance: {instanceId}, server: {ethStatsConfig.Server}");
            }
            MessageSender sender = new(instanceId, _api.LogManager);
            const int reconnectionInterval = 5000;
            const string api = "no";
            const string client = "0.1.1";
            const bool canUpdateHistory = false;
            string node = ProductInfo.ClientId;
            int port = networkConfig.P2PPort;
            string network = _api.SpecProvider!.NetworkId.ToString();
            string protocol = $"{P2PProtocolInfoProvider.DefaultCapabilitiesToString()}";

            _ethStatsClient = new EthStatsClient(
                ethStatsConfig.Server,
                reconnectionInterval,
                sender,
                _api.LogManager);

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
                _ethStatsClient,
                sender,
                getFromAPi.TxPool!,
                getFromAPi.BlockTree!,
                getFromAPi.PeerManager!,
                getFromAPi.GasPriceOracle!,
                getFromAPi.EthSyncingInfo!,
                initConfig.IsMining,
                TimeSpan.FromSeconds(ethStatsConfig.SendInterval),
                getFromAPi.LogManager);

            await _ethStatsIntegration.InitAsync();
        }
    }
}
