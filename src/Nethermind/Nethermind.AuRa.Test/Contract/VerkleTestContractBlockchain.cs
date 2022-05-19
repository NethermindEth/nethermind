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
    public class VerkleTestContractBlockchain : VerkleTestBlockchain
    {
        public ChainSpec ChainSpec { get; set; }
        
        protected VerkleTestContractBlockchain()
        {
            SealEngineType = Nethermind.Core.SealEngineType.AuRa;
        }

        public static async Task<TTest> ForTest<TTest, TTestClass>(string testSuffix = null) where TTest : VerkleTestContractBlockchain, new()
        {
            (ChainSpec ChainSpec, ISpecProvider SpecProvider) GetSpecProvider()
            {
                ChainSpecLoader loader = new(new EthereumJsonSerializer());
                string name = string.IsNullOrEmpty(testSuffix) ? $"{typeof(TTestClass).FullName}.json" : $"{typeof(TTestClass).FullName}.{testSuffix}.json";
                using Stream? stream = typeof(TTestClass).Assembly.GetManifestResourceStream(name);
                using StreamReader reader = new(stream ?? new MemoryStream());
                ChainSpec chainSpec = loader.Load(reader.ReadToEnd());
                ChainSpecBasedSpecProvider chainSpecBasedSpecProvider = new(chainSpec);
                return (chainSpec, chainSpecBasedSpecProvider);
            }

            (ChainSpec ChainSpec, ISpecProvider SpecProvider) provider = GetSpecProvider();
            TTest test = new() {ChainSpec = provider.ChainSpec};
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
