// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.JsonRpc.Modules;
using Nethermind.Config;
using Nethermind.Logging;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain;
using Nethermind.Taiko.Rpc;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.JsonRpc;
using Nethermind.HealthChecks;

namespace Nethermind.Taiko;

public class TaikoPlugin : INethermindPlugin
{
    public string Author => "Nethermind";
    public string Name => "Taiko";
    public string Description => "Taiko support for Nethermind";

    private ITaikoConfig? _taikoConfig;
    private NethermindApi? _api;
    private ILogger _logger;

    public bool ShouldRunSteps(INethermindApi api) => _taikoConfig?.Enabled == true && api.ChainSpec.SealEngineType == Core.SealEngineType.Taiko;

    public Task Init(INethermindApi api)
    {
        _taikoConfig = api.Config<ITaikoConfig>();

        if (!ShouldRunSteps(api))
            return Task.CompletedTask;

        _api = (NethermindApi)api;
        _logger = _api.LogManager.GetClassLogger();

        return Task.CompletedTask;
    }

    public async Task InitRpcModules()
    {
        if (_api is null || !ShouldRunSteps(_api))
            return;

        ArgumentNullException.ThrowIfNull(_api.SpecProvider);
        ArgumentNullException.ThrowIfNull(_api.BlockProcessingQueue);
        ArgumentNullException.ThrowIfNull(_api.SyncModeSelector);
        ArgumentNullException.ThrowIfNull(_api.BlockTree);
        ArgumentNullException.ThrowIfNull(_api.BlockValidator);
        ArgumentNullException.ThrowIfNull(_api.RpcModuleProvider);
        ArgumentNullException.ThrowIfNull(_api.ReceiptStorage);
        ArgumentNullException.ThrowIfNull(_api.StateReader);
        ArgumentNullException.ThrowIfNull(_api.TxPool);
        ArgumentNullException.ThrowIfNull(_api.TxSender);
        ArgumentNullException.ThrowIfNull(_api.Wallet);
        ArgumentNullException.ThrowIfNull(_api.GasPriceOracle);
        ArgumentNullException.ThrowIfNull(_api.EthSyncingInfo);

        // Ugly temporary hack to not receive engine API messages before end of processing of all blocks after restart.
        // Then we will wait 5s more to ensure everything is processed
        while (!_api.BlockProcessingQueue.IsEmpty)
            await Task.Delay(100);
        await Task.Delay(5000);


        _api.RpcCapabilitiesProvider = new EngineRpcCapabilitiesProvider(_api.SpecProvider);

        FeeHistoryOracle feeHistoryOracle = new FeeHistoryOracle(_api.BlockTree, _api.ReceiptStorage, _api.SpecProvider);
        _api.DisposeStack.Push(feeHistoryOracle);

        IInitConfig initConfig = _api.Config<IInitConfig>();
        TaikoRpcModule taikoRpc = new(
            _api.Config<IJsonRpcConfig>(),
            _api.CreateBlockchainBridge(),
            _api.BlockTree.AsReadOnly(),
            _api.ReceiptStorage,
            _api.StateReader,
            _api.TxPool,
            _api.TxSender,
            _api.Wallet,
            _api.LogManager,
            _api.SpecProvider,
            _api.GasPriceOracle,
            _api.EthSyncingInfo,
            feeHistoryOracle,
            _api.Config<IBlocksConfig>().SecondsPerSlot,
            _api.Config<ISyncConfig>()
            );

        _api.RpcModuleProvider.RegisterSingle((ITaikoRpcModule)taikoRpc);
        _api.RpcModuleProvider.RegisterSingle((ITaikoAuthRpcModule)taikoRpc);

        if (_logger.IsInfo) _logger.Info("Taiko Engine Module has been enabled");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public bool MustInitialize => true;
}
