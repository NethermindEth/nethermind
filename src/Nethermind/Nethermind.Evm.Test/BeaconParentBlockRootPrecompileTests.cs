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
using Nethermind.Core.Test.Blockchain;
using System.Threading.Tasks;

namespace Nethermind.Evm.Test;

public class Eip4788Tests : TestBlockchain
{
    private ISpecProvider specProvider;
    private SenderRecipientAndMiner senderRecipientAndMiner = SenderRecipientAndMiner.Default;
    protected static IEnumerable<(IReleaseSpec Spec, bool ShouldFail)> BeaconBlockRootGetPayloadV3ForDifferentSpecTestSource()
    {
        yield return (Shanghai.Instance, true);
        yield return (Cancun.Instance, false);
    }

    [TestCaseSource(nameof(BeaconBlockRootGetPayloadV3ForDifferentSpecTestSource))]
    public async Task BeaconBlockRoot_Is_Stored_Correctly_and_Only_Valid_PostCancun((IReleaseSpec Spec, bool ShouldFail) testCase)
    {
        specProvider = new TestSpecProvider(testCase.Spec);
        TestBlockchain testBlockchain = await base.Build(specProvider, addBlockOnStart: false);
        GethLikeBlockMemoryTracer? tracer = new(GethTraceOptions.Default);
        Block block = CreateBlock(testBlockchain.State, testCase.Spec);
        _ = testBlockchain.BlockProcessor.Process(
            testBlockchain.State.StateRoot,
            new List<Block> { block },
            ProcessingOptions.NoValidation,
            tracer);
        List<GethLikeTxTrace>? traces = tracer.BuildResult().ToList();
        Assert.That(testCase.ShouldFail, Is.EqualTo(traces[0].Failed));
    }

    Block CreateBlock(IWorldState testState, IReleaseSpec spec)
    {
        Keccak parentBeaconBlockRoot = TestItem.KeccakG;

        byte[] bytecode = Prepare
            .EvmCode
            .TIMESTAMP()
            .MSTORE(0)
            .CALL(100.Ether(), Address.FromNumber(0x0B), 0, 0, 32, 32, 32)
            .MLOAD(32)
            .EQ(new UInt256(parentBeaconBlockRoot.Bytes, true))
            .JUMPI(0x57)
            .INVALID()
            .JUMPDEST()
            .STOP()
            .Done;

        testState.InsertCode(TestBlockchain.AccountA, bytecode, specProvider.GenesisSpec);
        Transaction tx = Core.Test.Builders.Build.A.Transaction
            .WithGasLimit(1_000_000)
            .WithGasPrice(1)
            .WithValue(1)
            .WithSenderAddress(TestBlockchain.AccountB)
            .WithNonce(testState.GetNonce(TestBlockchain.AccountB))
            .To(TestBlockchain.AccountA)
            .TestObject;

        testState.Commit(spec);
        testState.CommitTree(0);
        testState.RecalculateStateRoot();
        BlockBuilder blockBuilder = Core.Test.Builders.Build.A.Block.Genesis
                .WithDifficulty(1)
                .WithTotalDifficulty(1L)
                .WithTransactions(tx)
                .WithPostMergeFlag(true);

        if (spec.IsBeaconBlockRootAvailable)
        {
            blockBuilder.WithParentBeaconBlockRoot(parentBeaconBlockRoot);
        }

        return blockBuilder.TestObject;
    }
}
