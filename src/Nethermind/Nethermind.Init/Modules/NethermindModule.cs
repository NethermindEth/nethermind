// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Autofac;
using Nethermind.Abi;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Spec;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.ServiceStopper;
using Nethermind.Core.Specs;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.LogIndex;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Monitoring.Config;
using Nethermind.Network.Config;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.TxPool;
using Nethermind.Wallet;
using Testably.Abstractions;

namespace Nethermind.Init.Modules;

/// <summary>
/// Full currently on production nethermind module, excluding plugins, and fallback to INethermindApi.
/// Not able to initialize all component without INethermindApi integration and running IStep correctly.
/// For testing without having to run ISteps, see <see cref="PseudoNethermindModule"/>.
/// </summary>
/// <param name="configProvider"></param>
public class NethermindModule(ChainSpec chainSpec, IConfigProvider configProvider, ILogManager logManager) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder
            .AddServiceStopper()
            .AddModule(new AppInputModule(chainSpec, configProvider, logManager))
            .AddModule(new NetworkModule(configProvider))
            .AddModule(new DiscoveryModule(configProvider.GetConfig<IInitConfig>(), configProvider.GetConfig<INetworkConfig>()))
            .AddModule(new DbModule(
                configProvider.GetConfig<IInitConfig>(),
                configProvider.GetConfig<IReceiptConfig>(),
                configProvider.GetConfig<ISyncConfig>()
            ))
            .AddModule(new DbMonitoringModule())
            .AddModule(new WorldStateModule(configProvider.GetConfig<IInitConfig>()))
            .AddModule(new PruningTrieStoreModule())
            .AddModule(new FlatWorldStateModule(configProvider.GetConfig<IFlatDbConfig>()))
            .AddModule(new WorldStateDbDeciderModule())
            .AddModule(new PrewarmerModule(configProvider.GetConfig<IBlocksConfig>()))
            .AddModule(new BuiltInStepsModule())
            .AddModule(new DatabaseMigrationsModule())
            .AddModule(new RpcModules(configProvider.GetConfig<IJsonRpcConfig>()))
            .AddModule(new Era1.EraModule())
            .AddModule(new EraE.EraEModule())
            .AddSource(new ConfigRegistrationSource())
            .AddModule(new BlockProcessingModule(configProvider.GetConfig<IInitConfig>(), configProvider.GetConfig<IBlocksConfig>()))
            .AddModule(new BlockTreeModule(configProvider.GetConfig<IReceiptConfig>(), configProvider.GetConfig<ILogIndexConfig>()))
            .AddModule(new KeyStoreModule())
            .AddModule(new MonitoringModule(configProvider.GetConfig<IMetricsConfig>()))
            .AddSingleton<ISpecProvider, ChainSpecBasedSpecProvider>()

            // Sequences deferred block-data flushing before state persistence (see IStatePersistenceBarrier).
            .AddSingleton<IStatePersistenceBarrier, StatePersistenceBarrier>()

            .AddKeyedSingleton<IProtectedPrivateKey>(IProtectedPrivateKey.NodeKey, (ctx) => ctx.Resolve<INodeKeyManager>().LoadNodeKey())
            .AddSingleton<IAbiEncoder>(AbiEncoder.Instance)
            .AddSingleton<IEciesCipher, EciesCipher>()
            .AddSingleton<ICryptoRandom, CryptoRandom>()

            .AddSingleton<IEthereumEcdsa, ISpecProvider>((specProvider) => new EthereumEcdsa(specProvider.ChainId))
            .Bind<IEcdsa, IEthereumEcdsa>()

            .AddSingleton<IChainHeadSpecProvider, ChainHeadSpecProvider>()
            .AddSingleton<IChainHeadInfoProvider, IChainHeadSpecProvider, IBlockTree, IStateReader>(
                (specProvider, blockTree, stateReader) => new ChainHeadInfoProvider(specProvider, blockTree, stateReader))
            .Add<IDisposableStack, AutofacDisposableStack>() // Not a singleton so that dispose is registered to correct lifetime

            .AddSingleton<IHardwareInfo, HardwareInfo>()

            .AddSingleton<ITimestamper>(_ => Timestamper.Default)
            .AddSingleton<ITimerFactory>(_ => TimerFactory.Default)
            .AddSingleton<IFileSystem>(_ => new RealFileSystem())
            .AddKeyedSingleton<IDriveInfo[]>(nameof(IInitConfig.BaseDbPath), (ctx) =>
            {
                IFileSystem fileSystem = ctx.Resolve<IFileSystem>();
                IInitConfig initConfig = ctx.Resolve<IInitConfig>();
                return fileSystem.GetDriveInfos(initConfig.BaseDbPath);
            })
            ;

        if (!configProvider.GetConfig<ITxPoolConfig>().BlobsSupport.IsPersistentStorage())
        {
            builder.AddSingleton<IBlobTxStorage>(NullBlobTxStorage.Instance);
        }

        if (configProvider.GetConfig<IReceiptConfig>().DeriveFromState)
        {
            ValidateReceiptDerivationConfig(configProvider);
            builder.AddModule(new ReceiptRegenerationModule());
        }
    }

    /// <summary>
    /// Refuses configurations under which receipt derivation would silently lose data.
    /// </summary>
    /// <remarks>
    /// Refused rather than warned: the first derived block stops writing bodies that cannot be reconstructed
    /// afterwards, so a node started on the wrong combination loses receipts permanently.
    /// </remarks>
    internal static void ValidateReceiptDerivationConfig(IConfigProvider configProvider)
    {
        if (!configProvider.GetConfig<IFlatDbConfig>().HistoryEnabled)
        {
            throw new InvalidConfigurationException(
                $"{nameof(IReceiptConfig.DeriveFromState)} requires Flat.{nameof(IFlatDbConfig.HistoryEnabled)}: receipt bodies are not written and can only be reproduced by re-executing over state history.", -1);
        }

        if (configProvider.GetConfig<ILogIndexConfig>().Enabled)
        {
            throw new InvalidConfigurationException(
                $"{nameof(IReceiptConfig.DeriveFromState)} cannot be combined with LogIndex.{nameof(ILogIndexConfig.Enabled)}: the index builder reads stored receipt bodies and would stall at the first derived block.", -1);
        }
    }

    // Just a wrapper to make it clear, these three are expected to be available at the time of configurations.
    private class AppInputModule(ChainSpec chainSpec, IConfigProvider configProvider, ILogManager logManager) : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            base.Load(builder);

            builder
                .AddSingleton(configProvider)
                .AddSingleton(chainSpec)
                .AddSingleton(logManager)
                .AddSingleton<ISpecProvider, ChainSpecBasedSpecProvider>()
                ;
        }
    }
}
