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
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

// [Parallelizable(ParallelScope.All)]
[TestFixture]
public class VerkleGenesisLoaderTests
{
    [TestCase]
    public void Can_load_genesis_with_emtpy_accounts_and_storage()
    {
        AssertBlockHash("0xb471de6da8eeac88addac8d2642efdb01a53df8f1e04d817e1f7c7d203bf0130", "Specs/empty_accounts_and_storages.json");
    }
    
    [Test]
    public void Can_load_genesis_with_emtpy_accounts_and_code()
    {
        AssertBlockHash("0x185038fbef089065bcd1b44e01e7c761d471b516db0080a3cbe03c57d3dbbf78", "Specs/empty_accounts_and_codes.json");
    }
    
    [Test]
    public void Can_load_genesis_with_precompile_that_has_zero_balance()
    {
        AssertBlockHash("0xd72860934ba158c136275b118207b39bbf811ef852b4ab6250544d7316781aef", "Specs/hive_zero_balance_test.json");
    }

    private void AssertBlockHash(string expectedHash, string chainspecFilePath)
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, chainspecFilePath);
        ChainSpec chainSpec = LoadChainSpec(path);
        IDb codeDb = new MemDb();
        VerkleTrieStore trieStore = new (DatabaseScheme.MemoryDb, LimboLogs.Instance);
        VerkleStateProvider stateProvider = new(trieStore, LimboLogs.Instance, codeDb);
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<long>()).Returns(Berlin.Instance);
        VerkleStorageProvider storageProvider = new(stateProvider, LimboLogs.Instance);
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
