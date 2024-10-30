// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

namespace Nethermind.Shutter;

public class ShutterPlugin : IConsensusWrapperPlugin, IInitializationPlugin
{
    public string Name => "Shutter";
    public string Description => "Shutter plugin for AuRa post-merge chains";
    public string Author => "Nethermind";
    public bool Enabled => ShouldRunSteps(_api!);
    public int Priority => PluginPriorities.Shutter;

    private INethermindApi? _api;
    private IMergeConfig? _mergeConfig;
    private IShutterConfig? _shutterConfig;
    private IBlocksConfig? _blocksConfig;
    private ShutterApi? _shutterApi;
    private ILogger _logger;
    private readonly CancellationTokenSource _cts = new();

    public class ShutterLoadingException(string message, Exception? innerException = null) : Exception(message, innerException);

    public Task Init(INethermindApi nethermindApi)
    {
        _api = nethermindApi;
        _blocksConfig = _api.Config<IBlocksConfig>();
        _mergeConfig = _api.Config<IMergeConfig>();
        _shutterConfig = _api.Config<IShutterConfig>();
        _logger = _api.LogManager.GetClassLogger();
        if (_logger.IsInfo) _logger.Info($"Initializing Shutter plugin.");
        return Task.CompletedTask;
    }

    public Task InitRpcModules()
    {
        if (Enabled)
        {
            if (_api!.BlockProducer is null) throw new ArgumentNullException(nameof(_api.BlockProducer));

            if (_logger.IsInfo) _logger.Info("Initializing Shutter block improvement.");
            _api.BlockImprovementContextFactory = _shutterApi!.GetBlockImprovementContextFactory(_api.BlockProducer);
        }
        return Task.CompletedTask;
    }

    public IBlockProducer InitBlockProducer(IBlockProducerFactory consensusPlugin, ITxSource? txSource)
    {
        if (Enabled)
        {
            if (_api!.BlockTree is null) throw new ArgumentNullException(nameof(_api.BlockTree));
            if (_api.EthereumEcdsa is null) throw new ArgumentNullException(nameof(_api.SpecProvider));
            if (_api.LogFinder is null) throw new ArgumentNullException(nameof(_api.LogFinder));
            if (_api.SpecProvider is null) throw new ArgumentNullException(nameof(_api.SpecProvider));
            if (_api.ReceiptFinder is null) throw new ArgumentNullException(nameof(_api.ReceiptFinder));
            if (_api.WorldStateManager is null) throw new ArgumentNullException(nameof(_api.WorldStateManager));
            if (_api.IpResolver is null) throw new ArgumentNullException(nameof(_api.IpResolver));

            if (_logger.IsInfo) _logger.Info("Initializing Shutter block producer.");

            Multiaddress[] bootnodeP2PAddresses;
            try
            {
                _shutterConfig!.Validate(out bootnodeP2PAddresses);
            }
            catch (ArgumentException e)
            {
                throw new ShutterLoadingException("Invalid Shutter config", e);
            }

            ShutterValidatorsInfo validatorsInfo = new();
            if (_shutterConfig!.ValidatorInfoFile is not null)
            {
                try
                {
                    validatorsInfo.Load(_shutterConfig!.ValidatorInfoFile);
                    validatorsInfo.Validate();
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
                _api.WorldStateManager,
                _api.FileSystem,
                _api.Config<IKeyStoreConfig>(),
                _shutterConfig,
                validatorsInfo,
                TimeSpan.FromSeconds(_blocksConfig!.SecondsPerSlot),
                _api.IpResolver.ExternalIp
            );

            _ = _shutterApi.StartP2P(bootnodeP2PAddresses, _cts);
        }

        return consensusPlugin.InitBlockProducer(_shutterApi is null ? txSource : _shutterApi.TxSource.Then(txSource));
    }

    public bool ShouldRunSteps(INethermindApi api)
    {
        _shutterConfig = api.Config<IShutterConfig>();
        _mergeConfig = api.Config<IMergeConfig>();
        return _shutterConfig!.Enabled && _mergeConfig!.Enabled && api.ChainSpec.SealEngineType is SealEngineType.AuRa;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Dispose();
        await (_shutterApi?.DisposeAsync() ?? default);
    }
}
