// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Autofac;
using Autofac.Core;
using Autofac.Core.Activators.Delegate;
using Autofac.Core.Lifetime;
using Autofac.Core.Registration;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.KeyStore;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.TxPool;
using Nethermind.Wallet;
using Nethermind.Specs;
using NSubstitute;
using Nethermind.Core;

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

                // Dynamically resolve any interface with nsubstitute
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
            api.BlockProducer = Substitute.For<IBlockProducer>();
            api.EngineSigner = Substitute.For<ISigner>();
            api.KeyStore = Substitute.For<IKeyStore>();
            api.ProtocolsManager = Substitute.For<IProtocolsManager>();
            api.ProtocolValidator = Substitute.For<IProtocolValidator>();
            api.TxSender = Substitute.For<ITxSender>();
            api.EngineSignerStore = Substitute.For<ISignerStore>();
            api.TransactionComparerProvider = Substitute.For<ITransactionComparerProvider>();
            api.BlockProductionPolicy = Substitute.For<IBlockProductionPolicy>();
        }
    }
}
