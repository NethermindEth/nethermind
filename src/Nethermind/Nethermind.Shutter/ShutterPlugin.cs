// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Abstractions;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Shutter.Config;
using Nethermind.Merge.Plugin;
using Nethermind.Logging;
using Autofac;
using Autofac.Core;
using Nethermind.Config;
using Nethermind.Abi;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade.Find;
using Nethermind.KeyStore.Config;
using Nethermind.Network;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Shutter;

public class ShutterPlugin(IShutterConfig shutterConfig, IMergeConfig mergeConfig, ChainSpec chainSpec) : IConsensusWrapperPlugin
{
    public string Name => "Shutter";
    public string Description => "Shutter plugin for AuRa post-merge chains";
    public string Author => "Nethermind";
    public bool Enabled => shutterConfig.Enabled && mergeConfig.Enabled && chainSpec.SealEngineType is SealEngineType.AuRa;
    public int Priority => PluginPriorities.Shutter;

    private ILogger _logger;

    public class ShutterLoadingException(string message, Exception? innerException = null) : Exception(message, innerException);

    public Task Init(INethermindApi nethermindApi)
    {
        _logger = nethermindApi.LogManager.GetClassLogger();
        if (_logger.IsInfo) _logger.Info($"Initializing Shutter plugin.");
        return Task.CompletedTask;
    }

    public Task InitRpcModules()
    {
        if (_logger.IsInfo) _logger.Info("Initializing Shutter block improvement.");
        return Task.CompletedTask;
    }

    public IBlockProducer InitBlockProducer(IBlockProducerFactory consensusPlugin)
    {
        if (_logger.IsInfo) _logger.Info("Initializing Shutter block producer.");
        return consensusPlugin.InitBlockProducer();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public IModule? Module => new ShutterPluginModule();
}

public class ShutterPluginModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder
            .AddStep(typeof(RunShutterP2P)) // Where it start the p2p

            .AddSingleton(CreateShutterApi)
            .Bind<IShutterApi, ShutterApi>()
            .AddDecorator<IBlockProducerTxSourceFactory, ShutterAdditionalBlockProductionTxSource>()
            .AddSingleton<IBlockImprovementContextFactory, ShutterApi, IBlockProducer>((api, blockProducer) => api.GetBlockImprovementContextFactory(blockProducer))
            ;
    }

    private ShutterApi CreateShutterApi(IComponentContext ctx)
    {
        IShutterConfig shutterConfig = ctx.Resolve<IShutterConfig>();
        IBlocksConfig blocksConfig = ctx.Resolve<IBlocksConfig>();

        ShutterValidatorsInfo validatorsInfo = new();
        if (shutterConfig!.ValidatorInfoFile is not null)
        {
            try
            {
                validatorsInfo.Load(shutterConfig!.ValidatorInfoFile);
            }
            catch (Exception e)
            {
                throw new ShutterPlugin.ShutterLoadingException("Could not load Shutter validator info file", e);
            }
        }

        return new ShutterApi(
            ctx.Resolve<IAbiEncoder>(),
            ctx.Resolve<IBlockTree>(),
            ctx.Resolve<IEthereumEcdsa>(),
            ctx.Resolve<ILogFinder>(),
            ctx.Resolve<IReceiptFinder>(),
            ctx.Resolve<ILogManager>(),
            ctx.Resolve<ISpecProvider>(),
            ctx.Resolve<ITimestamper>(),
            ctx.Resolve<IShareableTxProcessorSource>(),
            ctx.Resolve<IFileSystem>(),
            ctx.Resolve<IKeyStoreConfig>(),
            shutterConfig,
            validatorsInfo,
            TimeSpan.FromSeconds(blocksConfig!.SecondsPerSlot),
            ctx.Resolve<IIPResolver>().ExternalIp
        );
    }
}
