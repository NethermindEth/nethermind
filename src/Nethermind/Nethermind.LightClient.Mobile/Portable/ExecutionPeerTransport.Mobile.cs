// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using System.Security.Cryptography;
using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Spec;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Init.Modules;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.TxPool;
using Testably.Abstractions;

namespace Nethermind.LightClient;

internal sealed partial class ExecutionPeerTransport
{
    private static Module CreateModule(ChainSpec chainSpec, IConfigProvider config, ILogManager logs) =>
        new MobileExecutionModule(chainSpec, config, logs);

    private sealed class MobileExecutionModule(ChainSpec chainSpec, IConfigProvider config, ILogManager logs) : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            builder.RegisterInstance(chainSpec);
            builder.RegisterInstance(config).As<IConfigProvider>();
            builder.RegisterInstance(logs).As<ILogManager>().ExternallyOwned();
            builder.RegisterSource(new ConfigRegistrationSource());
            builder.RegisterModule(new NetworkModule(config));
            builder.RegisterModule(new DiscoveryModule(config.GetConfig<IInitConfig>(), config.GetConfig<INetworkConfig>()));
            builder.RegisterModule(new MobileExecutionOverrides());
        }
    }

    private sealed class MobileExecutionOverrides : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            builder.RegisterType<SnapEnrAdvertisement>()
                .WithParameter(new TypedParameter(typeof(IP2PCapabilityResolver[]), new IP2PCapabilityResolver[] { new LightCapabilities() }))
                .SingleInstance();
            builder.RegisterType<ChainSpecBasedSpecProvider>().As<ISpecProvider>().SingleInstance();
            builder.RegisterType<ChainHeadSpecProvider>().As<IChainHeadSpecProvider>().SingleInstance();
            builder.RegisterType<BlobCustodyTracker>().As<IBlobCustodyTracker>().SingleInstance();
            builder.RegisterType<CryptoRandom>().As<ICryptoRandom>().SingleInstance();
            builder.RegisterType<EciesCipher>().As<IEciesCipher>().SingleInstance();
            builder.Register(context => new EthereumEcdsa(context.Resolve<ISpecProvider>().ChainId))
                .As<IEthereumEcdsa>().As<IEcdsa>().SingleInstance();
            builder.RegisterInstance(Timestamper.Default).As<ITimestamper>();
            builder.RegisterInstance(TimerFactory.Default).As<ITimerFactory>();
            builder.RegisterType<RealFileSystem>().As<IFileSystem>().SingleInstance();
            builder.Register(LoadNodeKey)
                .Keyed<IProtectedPrivateKey>(IProtectedPrivateKey.NodeKey).SingleInstance();
            builder.RegisterInstance(new StaticSelector(SyncMode.Disconnected)).As<ISyncModeSelector>();
            builder.RegisterInstance(NullStateBoundary.Instance).As<IStateBoundary>();
            builder.RegisterType<BlockTreeMutationLock>().SingleInstance();

            string[] databases = [DbNames.Blocks, DbNames.Headers, DbNames.BlockNumbers, DbNames.BlockInfos, DbNames.Metadata, DbNames.BlockAccessLists];
            foreach (string name in databases)
                builder.Register(_ => new MemDb(name)).Keyed<IDb>(name).SingleInstance();
            builder.RegisterType<BlockStore>().As<IBlockStore>().WithAttributeFiltering().SingleInstance();
            builder.RegisterType<HeaderStore>().As<IHeaderStore>().WithAttributeFiltering().SingleInstance();
            builder.Register(_ => new BadBlockStore(new MemDb(), 0)).As<IBadBlockStore>().SingleInstance();
            builder.RegisterType<BlockAccessListStore>().As<IBlockAccessListStore>().WithAttributeFiltering().SingleInstance();
            builder.RegisterType<ChainLevelInfoRepository>().As<IChainLevelInfoRepository>().WithAttributeFiltering().SingleInstance();
            builder.RegisterType<BlockTree>().As<IBlockTree>().As<IBlockFinder>().WithAttributeFiltering().SingleInstance();
        }

        private static ProtectedPrivateKey LoadNodeKey(IComponentContext context)
        {
            IKeyStoreConfig config = context.Resolve<IKeyStoreConfig>();
            ICryptoRandom random = context.Resolve<ICryptoRandom>();
            string path = (config.EnodeKeyFile ?? "node.key.plain").GetApplicationResourcePath(config.KeyStoreDirectory);
            byte[]? encoded = File.Exists(path) ? File.ReadAllBytes(path) : null;
            try
            {
                using PrivateKeyGenerator generator = new(random);
                using PrivateKey key = encoded is null ? generator.Generate() : new(encoded);
                if (encoded is null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    FileStreamOptions options = OwnerOnlyFile.CreateWriteOptions();
                    options.Mode = FileMode.CreateNew;
                    using FileStream stream = new(path, options);
                    stream.Write(key.KeyBytes);
                }
                return new ProtectedPrivateKey(key, config.KeyStoreDirectory, random);
            }
            finally
            {
                if (encoded is not null) CryptographicOperations.ZeroMemory(encoded);
            }
        }
    }
}
