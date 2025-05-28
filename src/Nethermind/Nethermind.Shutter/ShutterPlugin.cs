// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Shutter.Config;
using Nethermind.Merge.Plugin;
using Nethermind.Logging;
using System.Threading;
using Nethermind.Config;
using Multiformats.Address;
using Nethermind.KeyStore.Config;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Shutter;

public class ShutterPlugin(IShutterConfig shutterConfig, IMergeConfig mergeConfig, ChainSpec chainSpec) : IConsensusWrapperPlugin
{
    public string Name => "Shutter";
    public string Description => "Shutter plugin for AuRa post-merge chains";
    public string Author => "Nethermind";
    public bool Enabled => shutterConfig.Enabled && mergeConfig.Enabled && chainSpec.SealEngineType is SealEngineType.AuRa;
    public int Priority => PluginPriorities.Shutter;

    private INethermindApi? _api;
    private IBlocksConfig? _blocksConfig;
    private ShutterApi? _shutterApi;
    private ILogger _logger;
    private readonly CancellationTokenSource _cts = new();

    public class ShutterLoadingException(string message, Exception? innerException = null) : Exception(message, innerException);

    public Task Init(INethermindApi nethermindApi)
    {
        _api = nethermindApi;
        _blocksConfig = _api.Config<IBlocksConfig>();
        _logger = _api.LogManager.GetClassLogger();
        if (_logger.IsInfo) _logger.Info($"Initializing Shutter plugin.");
        return Task.CompletedTask;
    }

    public Task InitRpcModules()
    {
        if (_api!.BlockProducer is null) throw new ArgumentNullException(nameof(_api.BlockProducer));

        if (_logger.IsInfo) _logger.Info("Initializing Shutter block improvement.");
        _api.BlockImprovementContextFactory = _shutterApi!.GetBlockImprovementContextFactory(_api.BlockProducer);
        return Task.CompletedTask;
    }

    public IBlockProducer InitBlockProducer(IBlockProducerFactory consensusPlugin, ITxSource? txSource)
    {
        if (_api!.BlockTree is null) throw new ArgumentNullException(nameof(_api.BlockTree));
        if (_api.EthereumEcdsa is null) throw new ArgumentNullException(nameof(_api.SpecProvider));
        ArgumentNullException.ThrowIfNull(_api.LogFinder);
        ArgumentNullException.ThrowIfNull(_api.SpecProvider);
        ArgumentNullException.ThrowIfNull(_api.ReceiptFinder);
        ArgumentNullException.ThrowIfNull(_api.WorldStateManager);
        ArgumentNullException.ThrowIfNull(_api.IpResolver);

        if (_logger.IsInfo) _logger.Info("Initializing Shutter block producer.");

        IEnumerable<Multiaddress> bootnodeP2PAddresses;
        try
        {
            shutterConfig!.Validate(out bootnodeP2PAddresses);
        }
        catch (ArgumentException e)
        {
            throw new ShutterLoadingException("Invalid Shutter config", e);
        }

        ShutterValidatorsInfo validatorsInfo = new();
        if (shutterConfig!.ValidatorInfoFile is not null)
        {
            try
            {
                validatorsInfo.Load(shutterConfig!.ValidatorInfoFile);
            }
            catch (Exception e)
            {
                throw new ShutterLoadingException("Could not load Shutter validator info file", e);
            }
        }

        _shutterApi = new ShutterApi(
            _api.AbiEncoder,
            _api.BlockTree,
            _api.EthereumEcdsa,
            _api.LogFinder,
            _api.ReceiptFinder,
            _api.LogManager,
            _api.SpecProvider,
            _api.Timestamper,
            _api.ReadOnlyTxProcessingEnvFactory,
            _api.FileSystem,
            _api.Config<IKeyStoreConfig>(),
            shutterConfig,
            validatorsInfo,
            TimeSpan.FromSeconds(_blocksConfig!.SecondsPerSlot),
            _api.IpResolver.ExternalIp
        );

        _ = _shutterApi.StartP2P(bootnodeP2PAddresses, _cts);

        return consensusPlugin.InitBlockProducer(_shutterApi is null ? txSource : _shutterApi.TxSource.Then(txSource));
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Dispose();
        await (_shutterApi?.DisposeAsync() ?? default);
    }
}
