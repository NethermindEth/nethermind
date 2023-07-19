// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db.Blooms;
using Nethermind.Db;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State.Repositories;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NUnit.Framework;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing.GethStyle;

namespace Nethermind.Evm.Test;

public class Eip4788Tests
{
    private WorldState _testState;
    private ISpecProvider specProvider;
    private SenderRecipientAndMiner senderRecipientAndMiner = SenderRecipientAndMiner.Default;
    protected static IEnumerable<(IReleaseSpec Spec, bool ShouldFail)> BeacondStateRootGetPayloadV3ForDifferentSpecTestSource()
    {
        yield return (Shanghai.Instance, true);
        yield return (Cancun.Instance, false);
    }

    [TestCaseSource(nameof(BeacondStateRootGetPayloadV3ForDifferentSpecTestSource))]
    public void ParentBeaconBlockRoot_Is_Stored_Correctly_and_Only_Valid_PostCancun((IReleaseSpec Spec, bool ShouldFail) testCase)
    {
        specProvider = new TestSpecProvider(testCase.Spec);
        DbProvider dbProvider = new(DbModeHint.Mem);
        dbProvider.RegisterDb(DbNames.BlockInfos, new MemDb());
        dbProvider.RegisterDb(DbNames.Blocks, new MemDb());
        dbProvider.RegisterDb(DbNames.Headers, new MemDb());
        dbProvider.RegisterDb(DbNames.State, new MemDb());
        dbProvider.RegisterDb(DbNames.Code, new MemDb());
        dbProvider.RegisterDb(DbNames.Metadata, new MemDb());
        BlockTree blockTree = new(
            dbProvider,
            new ChainLevelInfoRepository(dbProvider),
            specProvider,
            NullBloomStorage.Instance,
            LimboLogs.Instance);
        TrieStore trieStore = new(
            dbProvider.RegisteredDbs[DbNames.State],
            NoPruning.Instance,
            Archive.Instance,
            LimboLogs.Instance);
        _testState = new(
            trieStore,
            dbProvider.RegisteredDbs[DbNames.Code],
            LimboLogs.Instance);
        StateReader stateReader = new(trieStore, dbProvider.GetDb<IDb>(DbNames.State), LimboLogs.Instance);
        BlockhashProvider blockhashProvider = new(blockTree, LimboLogs.Instance);

        VirtualMachine virtualMachine = new(
            blockhashProvider,
            specProvider,
            LimboLogs.Instance);
        TransactionProcessor txProcessor = new(
            specProvider,
            _testState,
            virtualMachine,
            LimboLogs.Instance);
        BlockProcessor blockProcessor = new(
            specProvider,
            Always.Valid,
            NoBlockRewards.Instance,
            new BlockProcessor.BlockValidationTransactionsExecutor(txProcessor, _testState),
            _testState,
            NullReceiptStorage.Instance,
            NullWitnessCollector.Instance,
            LimboLogs.Instance);



        GethLikeBlockTracer? tracer = new(GethTraceOptions.Default);
        Block block = CreateBlock();
        Block[] blocks = blockProcessor.Process(
            Keccak.EmptyTreeHash,
            new List<Block> { block },
            ProcessingOptions.None,
            tracer);
        List<GethLikeTxTrace>? traces = tracer.BuildResult().ToList();
        Assert.Equals(traces[0].Failed, testCase.ShouldFail);
    }

    Block CreateBlock()
    {
        if (!_testState.AccountExists(senderRecipientAndMiner.Sender))
            _testState.CreateAccount(senderRecipientAndMiner.Sender, 100.Ether());
        else
            _testState.AddToBalance(senderRecipientAndMiner.Sender, 100.Ether(), specProvider.GenesisSpec);

        if (!_testState.AccountExists(senderRecipientAndMiner.Recipient))
            _testState.CreateAccount(senderRecipientAndMiner.Recipient, 100.Ether());
        else
            _testState.AddToBalance(senderRecipientAndMiner.Recipient, 100.Ether(), specProvider.GenesisSpec);

        byte[] bytecode = Prepare
            .EvmCode
            .TIMESTAMP()
            .MSTORE(0)
            .CALL(100.GWei(), Address.FromNumber(0x0B), 0, 0, 32, 32, 32)
            .MLOAD(32)
            .EQ(new UInt256(TestItem.KeccakG.Bytes, true))
            .JUMPI(0x53)
            .INVALID()
            .JUMPDEST()
            .STOP()
            .Done;

        _testState.InsertCode(senderRecipientAndMiner.Recipient, bytecode, specProvider.GenesisSpec);
        Transaction tx = Build.A.Transaction
            .WithGasLimit(1_000_000)
            .WithGasPrice(1)
            .WithValue(1)
            .WithData(bytecode)
            .WithSenderAddress(senderRecipientAndMiner.Sender)
            .WithNonce(_testState.GetNonce(senderRecipientAndMiner.Sender))
            .To(senderRecipientAndMiner.Recipient)
            .TestObject;

        _testState.RecalculateStateRoot();
        Block block0 =
            Build.A.Block.Genesis
                .WithDifficulty(1)
                .WithTotalDifficulty(1L)
                .WithTransactions(tx)
                .WithPostMergeFlag(true)
                .WithParentBeaconBlockRoot(TestItem.KeccakG)
                .TestObject;
        return block0;
    }
}
