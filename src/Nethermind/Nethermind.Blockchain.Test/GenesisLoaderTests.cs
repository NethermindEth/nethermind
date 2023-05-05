// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

[Parallelizable(ParallelScope.All)]
[TestFixture]
public class GenesisLoaderTests
{
    [Test, Timeout(Timeout.MaxTestTime)]
    public void Can_load_genesis_with_emtpy_accounts_and_storage()
    {
        AssertBlockHash("0x61b2253366eab37849d21ac066b96c9de133b8c58a9a38652deae1dd7ec22e7b", "Specs/empty_accounts_and_storages.json");
    }

    [Test, Timeout(Timeout.MaxTestTime)]
    public void Can_load_genesis_with_emtpy_accounts_and_code()
    {
        AssertBlockHash("0xfa3da895e1c2a4d2673f60dd885b867d60fb6d823abaf1e5276a899d7e2feca5", "Specs/empty_accounts_and_codes.json");
    }

    [Test, Timeout(Timeout.MaxTestTime)]
    public void Can_load_genesis_with_precompile_that_has_zero_balance()
    {
        AssertBlockHash("0x62839401df8970ec70785f62e9e9d559b256a9a10b343baf6c064747b094de09", "Specs/hive_zero_balance_test.json");
    }

    [Test, Timeout(Timeout.MaxTestTime)]
    public void Can_load_withdrawals_with_empty_root()
    {
        Block block = GetGenesisBlock("Specs/shanghai_from_genesis.json");
        Assert.That(block.Hash!.ToString(), Is.EqualTo("0x1326aad1114b1f1c6a345b69ba4ba6f8ab6ce027d988aacd275ab596a047a547"));
    }

    private void AssertBlockHash(string expectedHash, string chainspecFilePath)
    {
        Block block = GetGenesisBlock(chainspecFilePath);
        Assert.That(block.Hash!.ToString(), Is.EqualTo(expectedHash));
    }

    private Block GetGenesisBlock(string chainspecPath)
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, chainspecPath);
        ChainSpec chainSpec = LoadChainSpec(path);
        IDb stateDb = new MemDb();
        IDb codeDb = new MemDb();
        TrieStore trieStore = new(stateDb, LimboLogs.Instance);
        IStateProvider stateProvider = new StateProvider(trieStore, codeDb, LimboLogs.Instance);
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<BlockHeader>()).Returns(Berlin.Instance);
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(Berlin.Instance);
        StorageProvider storageProvider = new(trieStore, stateProvider, LimboLogs.Instance);
        ITransactionProcessor transactionProcessor = Substitute.For<ITransactionProcessor>();
        GenesisLoader genesisLoader = new(chainSpec, specProvider, stateProvider, storageProvider,
            transactionProcessor);
        return genesisLoader.Load();
    }


    private static ChainSpec LoadChainSpec(string path)
    {
        string data = File.ReadAllText(path);
        ChainSpecLoader chainSpecLoader = new(new EthereumJsonSerializer());
        ChainSpec chainSpec = chainSpecLoader.Load(data);
        return chainSpec;
    }
}
