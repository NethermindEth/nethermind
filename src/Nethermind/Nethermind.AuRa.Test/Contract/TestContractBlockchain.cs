//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System.IO;
using System.Threading.Tasks;
using Castle.Core.Internal;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.AuRa.Test.Contract
{
    public class TestContractBlockchain : TestBlockchain
    {
        public ChainSpec ChainSpec { get; set; }
        
        protected TestContractBlockchain()
        {
            SealEngineType = Nethermind.Core.SealEngineType.AuRa;
        }

        public static async Task<TTest> ForTest<TTest, TTestClass>(string testSuffix = null) where TTest : TestContractBlockchain, new()
        {
            (ChainSpec ChainSpec, ISpecProvider SpecProvider) GetSpecProvider()
            {
                ChainSpecLoader loader = new ChainSpecLoader(new EthereumJsonSerializer());
                var name = string.IsNullOrEmpty(testSuffix) ? $"{typeof(TTestClass).FullName}.json" : $"{typeof(TTestClass).FullName}.{testSuffix}.json";
                using var stream = typeof(TTestClass).Assembly.GetManifestResourceStream(name);
                using var reader = new StreamReader(stream ?? new MemoryStream());
                var chainSpec = loader.Load(reader.ReadToEnd());
                ChainSpecBasedSpecProvider chainSpecBasedSpecProvider = new ChainSpecBasedSpecProvider(chainSpec);
                return (chainSpec, chainSpecBasedSpecProvider);
            }

            var provider = GetSpecProvider();
            var test = new TTest() {ChainSpec = provider.ChainSpec};
            return (TTest) await test.Build(provider.SpecProvider);
        }

        protected override Block GetGenesisBlock() =>
            new GenesisLoader(
                    ChainSpec,
                    SpecProvider,
                    State,
                    Storage,
                    TxProcessor)
                .Load();
    }
}
