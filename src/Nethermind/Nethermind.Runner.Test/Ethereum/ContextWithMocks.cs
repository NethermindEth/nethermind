// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using Autofac;
using Autofac.Core;
using Autofac.Core.Activators.Delegate;
using Autofac.Core.Lifetime;
using Autofac.Core.Registration;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Services;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Db.Blooms;
using Nethermind.Grpc;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.KeyStore;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State.Repositories;
using Nethermind.TxPool;
using Nethermind.Wallet;
using Nethermind.Sockets;
using Nethermind.Specs;
using Nethermind.Trie;
using NSubstitute;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Facade.Find;

namespace Nethermind.Runner.Test.Ethereum
{
    public static class Build
    {
        public static NethermindApi ContextWithMocks()
        {
            NethermindApi.Dependencies apiDependencies = new NethermindApi.Dependencies(
                Substitute.For<IConfigProvider>(),
                Substitute.For<IJsonSerializer>(),
                LimboLogs.Instance,
                new ChainSpec { Parameters = new ChainParameters(), },
                Substitute.For<ISpecProvider>(),
                [],
                Substitute.For<IProcessExitSource>(),
                new ContainerBuilder()
                    .AddSingleton<ITxValidator>(new TxValidator(MainnetSpecProvider.Instance.ChainId))
                    .AddSource(new NSubstituteRegistrationSource())
                    .Build()
            );

            var api = new NethermindApi(apiDependencies);
            MockOutNethermindApi(api);
            api.NodeStorageFactory = new NodeStorageFactory(INodeStorage.KeyScheme.HalfPath, LimboLogs.Instance);
            return api;
        }

        private class NSubstituteRegistrationSource : IRegistrationSource
        {
            public IEnumerable<IComponentRegistration> RegistrationsFor(Service service, Func<Service, IEnumerable<ServiceRegistration>> registrationAccessor)
            {
                if (registrationAccessor(service).Any())
                {
                    // Already have registration
                    return [];
                }

                IServiceWithType swt = service as IServiceWithType;
                if (registrationAccessor(service).Any() || swt == null || !swt.ServiceType.IsInterface)
                {
                    // It's not a request for the base handler type, so skip it.
                    return [];
                }

                // Dynamically resolve any interface with nsubstitue
                ComponentRegistration registration = new ComponentRegistration(
                    Guid.NewGuid(),
                    new DelegateActivator(swt.ServiceType, (c, p) =>
                    {
                        return Substitute.For([swt.ServiceType], []);
                    }),
                    new RootScopeLifetime(),
                    InstanceSharing.Shared,
                    InstanceOwnership.OwnedByLifetimeScope,
                    new[] { service },
                    new Dictionary<string, object>());

                return [registration];
            }

            public bool IsAdapterForIndividualComponents => false;
        }

        public static void MockOutNethermindApi(NethermindApi api)
        {
            api.Enode = Substitute.For<IEnode>();
            api.TxPool = Substitute.For<ITxPool>();
            api.Wallet = Substitute.For<IWallet>();
            api.BlockTree = Substitute.For<IBlockTree>();
            api.DbProvider = TestMemDbProvider.Init();
            api.EthereumEcdsa = Substitute.For<IEthereumEcdsa>();
            api.ReceiptStorage = Substitute.For<IReceiptStorage>();
            api.ReceiptFinder = Substitute.For<IReceiptFinder>();
            api.RewardCalculatorSource = Substitute.For<IRewardCalculatorSource>();
            api.TxPoolInfoProvider = Substitute.For<ITxPoolInfoProvider>();
            api.BloomStorage = Substitute.For<IBloomStorage>();
            api.Sealer = Substitute.For<ISealer>();
            api.BlockProducer = Substitute.For<IBlockProducer>();
            api.EngineSigner = Substitute.For<ISigner>();
            api.FileSystem = Substitute.For<IFileSystem>();
            api.FilterManager = Substitute.For<IFilterManager>();
            api.FilterStore = Substitute.For<IFilterStore>();
            api.GrpcServer = Substitute.For<IGrpcServer>();
            api.IpResolver = Substitute.For<IIPResolver>();
            api.KeyStore = Substitute.For<IKeyStore>();
            api.LogFinder = Substitute.For<ILogFinder>();
            api.ProtocolsManager = Substitute.For<IProtocolsManager>();
            api.ProtocolValidator = Substitute.For<IProtocolValidator>();
            api.SealValidator = Substitute.For<ISealValidator>();
            api.MainProcessingContext = Substitute.For<IMainProcessingContext>();
            api.TxSender = Substitute.For<ITxSender>();
            api.BlockProcessingQueue = Substitute.For<IBlockProcessingQueue>();
            api.EngineSignerStore = Substitute.For<ISignerStore>();
            api.WebSocketsManager = Substitute.For<IWebSocketsManager>();
            api.ChainLevelInfoRepository = Substitute.For<IChainLevelInfoRepository>();
            api.BlockProducerEnvFactory = Substitute.For<IBlockProducerEnvFactory>();
            api.TransactionComparerProvider = Substitute.For<ITransactionComparerProvider>();
            api.GasPriceOracle = Substitute.For<IGasPriceOracle>();
            api.HealthHintService = Substitute.For<IHealthHintService>();
            api.BlockProductionPolicy = Substitute.For<IBlockProductionPolicy>();
            api.ReceiptMonitor = Substitute.For<IReceiptMonitor>();
            api.BadBlocksStore = Substitute.For<IBadBlockStore>();

            api.NodeStorageFactory = new NodeStorageFactory(INodeStorage.KeyScheme.HalfPath, LimboLogs.Instance);
        }
    }
}
