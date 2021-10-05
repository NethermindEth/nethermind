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
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    [Parallelizable(ParallelScope.All)]
    [TestFixture]
    public class GenesisLoaderTests
    {
        [TestCase]
        public void Can_load_genesis_with_emtpy_accounts_and_storage()
        {
            AssertBlockHash("0x61b2253366eab37849d21ac066b96c9de133b8c58a9a38652deae1dd7ec22e7b", "Specs/empty_accounts_and_storages.json");
        }
        
        [Test]
        public void Can_load_genesis_with_emtpy_accounts_and_code()
        {
            AssertBlockHash("0xfa3da895e1c2a4d2673f60dd885b867d60fb6d823abaf1e5276a899d7e2feca5", "Specs/empty_accounts_and_codes.json");
        }
        
        [Test]
        public void Can_load_genesis_with_precompile_that_has_zero_balance()
        {
            AssertBlockHash("0x62839401df8970ec70785f62e9e9d559b256a9a10b343baf6c064747b094de09", "Specs/hive_zero_balance_test.json");
        }

        private void AssertBlockHash(string expectedHash, string chainspecFilePath)
        {
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, chainspecFilePath);
            ChainSpec chainSpec = LoadChainSpec(path);
            IDb stateDb = new MemDb();
            IDb codeDb = new MemDb();
            TrieStore trieStore = new(stateDb, LimboLogs.Instance);
            IStateProvider stateProvider = new StateProvider(trieStore, codeDb, LimboLogs.Instance);
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();
            specProvider.GetSpec(Arg.Any<long>()).Returns(Berlin.Instance);
            StorageProvider storageProvider = new(trieStore, stateProvider, LimboLogs.Instance);
            ITransactionProcessor transactionProcessor = Substitute.For<ITransactionProcessor>();
            GenesisLoader genesisLoader = new(chainSpec, specProvider, stateProvider, storageProvider,
                transactionProcessor);
            Block block = genesisLoader.Load();
            Assert.AreEqual(expectedHash, block.Hash!.ToString());
        }

        
        private static ChainSpec LoadChainSpec(string path)
        {
            string data = File.ReadAllText(path);
            ChainSpecLoader chainSpecLoader = new(new EthereumJsonSerializer());
            ChainSpec chainSpec = chainSpecLoader.Load(data);
            return chainSpec;
        }
    }
}
