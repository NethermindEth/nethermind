// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Shutter.Config;
using Nethermind.Merge.Plugin;
using Nethermind.Logging;
using System.IO;
using Nethermind.Serialization.Json;
using System.Threading;

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
    private ShutterApi? _shutterApi;
    private ILogger _logger;
    private readonly CancellationTokenSource _cts = new();

    public class ShutterLoadingException(string message, Exception? innerException = null) : Exception(message, innerException);

    public Task Init(INethermindApi nethermindApi)
    {
        _api = nethermindApi;
        _mergeConfig = _api.Config<IMergeConfig>();
        _shutterConfig = _api.Config<IShutterConfig>();
        _logger = _api.LogManager.GetClassLogger();
        _logger.Info($"Initializing Shutter plugin.");
        return Task.CompletedTask;
    }

    public Task InitRpcModules()
    {
        if (Enabled)
        {
            if (_api!.BlockProducer is null) throw new ArgumentNullException(nameof(_api.BlockProducer));

            _logger.Info("Initializing Shutter block improvement.");
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

            _logger.Info("Initializing Shutter block producer.");

            _shutterConfig!.Validate();

            Dictionary<ulong, byte[]> validatorsInfo = [];
            if (_shutterConfig!.ValidatorInfoFile is not null)
            {
                try
                {
                    validatorsInfo = LoadValidatorInfo(_shutterConfig!.ValidatorInfoFile);
                }
                catch (Exception e)
                {
                    throw new ShutterLoadingException("Could not load Shutter validator info file", e);
                }
            }

            _shutterApi = new ShutterApi(
                _api.AbiEncoder,
                _api.BlockTree.AsReadOnly(),
                _api.EthereumEcdsa,
                _api.LogFinder,
                _api.ReceiptFinder,
                _api.LogManager,
                _api.SpecProvider,
                _api.Timestamper,
                _api.WorldStateManager,
                _shutterConfig,
                validatorsInfo
            );

            _shutterApi.StartP2P(_cts);
        }

        ITxSource? compositeTxSource = _shutterApi is null ? txSource : _shutterApi.TxSource.Then(txSource);
        return consensusPlugin.InitBlockProducer(compositeTxSource);
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

    private static Dictionary<ulong, byte[]> LoadValidatorInfo(string fp)
    {
        FileStream fstream = new FileStream(fp, FileMode.Open, FileAccess.Read, FileShare.None);
        return new EthereumJsonSerializer().Deserialize<Dictionary<ulong, byte[]>>(fstream);
    }
}
