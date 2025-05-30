// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;

namespace Nethermind.AuRa.Test.Contract
{
    public class TestContractBlockchain : TestBlockchain
    {
        public ChainSpec? ChainSpecOverride { get; set; }

        protected TestContractBlockchain()
        {
            SealEngineType = Nethermind.Core.SealEngineType.AuRa;
        }

        public static async Task<TTest> ForTest<TTest, TTestClass>(string? testSuffix = null) where TTest : TestContractBlockchain, new()
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
            TTest test = new() { ChainSpecOverride = provider.ChainSpec };
            return (TTest)await test.Build(builder =>
                builder.AddSingleton<ISpecProvider>(provider.SpecProvider));
        }

        protected override ChainSpec CreateChainSpec()
        {
            return ChainSpecOverride ?? base.CreateChainSpec();
        }

        protected override Block GetGenesisBlock(IWorldState worldState) =>
            new GenesisLoader(
                    ChainSpec,
                    SpecProvider,
                    worldState,
                    TxProcessor)
                .Load();
    }
}
