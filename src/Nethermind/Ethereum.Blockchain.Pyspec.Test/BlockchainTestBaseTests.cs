// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autofac;
using Ethereum.Test.Base;
using Nethermind.Blockchain.Find;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Test;
using Nethermind.Trie;
using NUnit.Framework;

namespace Ethereum.Blockchain.Pyspec.Test;

public class BlockchainTestBaseTests
{
    [Test]
    public async Task Amsterdam_genesis_header_keeps_frontier_style_prestate_initialization()
    {
        Dictionary<Address, AccountState> preState = new()
        {
            [new Address("0xfffffffffffffffffffffffffffffffffffffffe")] = new()
        };

        Hash256 genesisStateRoot = CalculateGenesisStateRoot(preState, Nethermind.Specs.Forks.Frontier.Instance);
        TestBlockHeaderJson genesisHeader = CreateAmsterdamGenesisHeader(genesisStateRoot);

        BlockchainTest test = new()
        {
            Name = nameof(Amsterdam_genesis_header_keeps_frontier_style_prestate_initialization),
            Network = Nethermind.Specs.Forks.Amsterdam.Instance,
            GenesisBlockHeader = genesisHeader,
            LastBlockHash = new Hash256(genesisHeader.Hash),
            Blocks = [],
            Pre = preState,
            PostStateRoot = genesisStateRoot,
        };

        BlockchainTestBaseProbe probe = new();
        EthereumTestResult result = await probe.RunAsync(test);

        Assert.That(result.Pass, Is.True);
    }

    private static Hash256 CalculateGenesisStateRoot(
        Dictionary<Address, AccountState> preState,
        IReleaseSpec genesisSpec)
    {
        TestSpecProvider specProvider = new(genesisSpec);
        IConfigProvider configProvider = new ConfigProvider();
        ILogManager logManager = new TestLogManager(LogLevel.Warn);

        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(configProvider))
            .AddSingleton<IBlockhashProvider>(new TestBlockhashProvider())
            .AddSingleton<ISpecProvider>(specProvider)
            .AddSingleton<ILogManager>(logManager)
            .Build();

        IWorldState stateProvider = container.Resolve<IMainProcessingContext>().WorldState;
        using IDisposable _ = stateProvider.BeginScope(null);

        GeneralStateTestBase.InitializeTestState(preState, stateProvider, specProvider);

        return stateProvider.StateRoot;
    }

    private static TestBlockHeaderJson CreateAmsterdamGenesisHeader(Hash256 stateRoot)
    {
        TestBlockHeaderJson header = new()
        {
            ParentHash = Keccak.Zero.ToString(),
            UncleHash = Keccak.OfAnEmptySequenceRlp.ToString(),
            Coinbase = Address.Zero.ToString(),
            StateRoot = stateRoot.ToString(),
            TransactionsTrie = PatriciaTree.EmptyTreeHash.ToString(),
            ReceiptTrie = PatriciaTree.EmptyTreeHash.ToString(),
            Bloom = Bloom.Empty.Bytes.ToHexString(true),
            Difficulty = "0x00",
            Number = "0x00",
            GasLimit = "0x01c9c380",
            GasUsed = "0x00",
            Timestamp = "0x00",
            ExtraData = "0x00",
            MixHash = Keccak.Zero.ToString(),
            Nonce = "0x0000000000000000",
            Hash = Keccak.Zero.ToString(),
            BaseFeePerGas = "0x07",
            WithdrawalsRoot = PatriciaTree.EmptyTreeHash.ToString(),
            ParentBeaconBlockRoot = Keccak.Zero.ToString(),
            BlobGasUsed = "0x00",
            ExcessBlobGas = "0x00",
            RequestsHash = ExecutionRequestExtensions.EmptyRequestsHash.ToString(),
            BlockAccessListHash = Keccak.OfAnEmptySequenceRlp.ToString(),
        };

        BlockHeader blockHeader = JsonToEthereumTest.Convert(header);
        header.Hash = blockHeader.CalculateHash().ToString();

        return header;
    }

    private sealed class BlockchainTestBaseProbe : BlockchainTestBase
    {
        public Task<EthereumTestResult> RunAsync(BlockchainTest test) => RunTest(test);
    }
}
