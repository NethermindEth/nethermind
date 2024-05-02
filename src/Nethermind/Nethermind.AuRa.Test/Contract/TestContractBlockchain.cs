// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Module = Autofac.Module;

namespace Nethermind.AuRa.Test.Contract
{
    public class TestContractBlockchain : TestBlockchain
    {
        private ChainSpec _overrideChainSpec;

        protected TestContractBlockchain()
        {
        }

        protected override void ConfigureContainer(ContainerBuilder builder)
        {
            base.ConfigureContainer(builder);
            builder.RegisterInstance(_overrideChainSpec);
        }

        public static async Task<TTest> ForTest<TTest, TTestClass>(string testSuffix = null) where TTest : TestContractBlockchain, new()
        {
            (ChainSpec ChainSpec, ISpecProvider SpecProvider) GetSpecProvider()
            {
                ChainSpecLoader loader = new(new EthereumJsonSerializer());
                string name = string.IsNullOrEmpty(testSuffix) ? $"{typeof(TTestClass).FullName}.json" : $"{typeof(TTestClass).FullName}.{testSuffix}.json";
                using Stream? stream = typeof(TTestClass).Assembly.GetManifestResourceStream(name);
                ChainSpec chainSpec = loader.Load(stream);
                ChainSpecBasedSpecProvider chainSpecBasedSpecProvider = new(chainSpec);
                return (chainSpec, chainSpecBasedSpecProvider);
            }

            (ChainSpec ChainSpec, ISpecProvider SpecProvider) provider = GetSpecProvider();
            TTest test = new()
            {
                _overrideChainSpec = provider.ChainSpec,
            };
            return (TTest)await test.Build(provider.SpecProvider);
        }

        protected override Block GetGenesisBlock() =>
            new GenesisLoader(
                    base.Container.Resolve<ChainSpec>(),
                    SpecProvider,
                    State,
                    TxProcessor)
                .Load();
    }
}
