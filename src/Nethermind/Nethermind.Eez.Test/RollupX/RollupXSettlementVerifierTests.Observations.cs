// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Evm;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;
using SignedInt256 = Nethermind.Int256.Int256;

namespace Nethermind.Eez.Test.RollupX;

public partial class RollupXSettlementVerifierTests
{
    [Test]
    public void Verify_RevertedSourceObservations_RequiresSingleAction([Values] bool multiple)
    {
        Transaction user = User();
        Block block = Build.A.Block.WithParentHash(TestItem.KeccakA).WithTransactions(user).TestObject;
        RollupUpdate anchor = new(1, TestItem.KeccakA.ValueHash256, block.Hash!.ValueHash256, SignedInt256.Zero);
        List<ExecutionEntry> entries = [new([anchor], default, [], [], RollingHash.SeedL1(anchor, default), 1, true, [])];
        List<StreamMessage> messages = [new StreamMessage.Initiate(0, ReadOnlyMemory<byte>.Empty), new StreamMessage.Snapshot()];
        for (int i = 0; i < (multiple ? 2 : 1); i++)
        {
            StreamMessage.Call call = Call(1, 0, (byte)(i + 1));
            ValueHash256 hash = Hash(call, 0);
            RollupUpdate update = new(1, block.Hash.ValueHash256, block.Hash.ValueHash256, SignedInt256.Zero);
            entries.Add(new([update], hash, [], [], RollingHash.SeedL1(update, hash), 1, true, [0x45]));
            messages.Add(call);
            messages.Add(Result(0x45));
        }
        messages.Add(new StreamMessage.Revert());
        messages.Add(new StreamMessage.Finish());
        PostBatch batch = new([], entries.ToArray(), [], 1, 0, [], [], [], [], [], 1, false);
        EezStatelessBlockResult result = ObservationBlock(block);
        NativeStream stream = new(ObservationOperations(block), messages.ToArray());

        if (multiple)
            Assert.That(() => RollupXSettlementVerifier.Verify(batch, stream, [result], 1),
                Throws.TypeOf<EezSettlementException>().With.Message.Contains("multi-action rollback"));
        else Assert.That(RollupXSettlementVerifier.Verify(batch, stream, [result], 1), Has.Length.EqualTo(1));
    }

    [Test]
    public void Verify_StaticCallback_BindsCompleteEntryAndReturnsReplayClaim([Values] bool corrupt)
    {
        Block block = Build.A.Block.WithParentHash(TestItem.KeccakA).WithTransactions(User()).TestObject;
        StreamMessage.StaticCall root = new(1, TestItem.AddressC, TestItem.AddressB, 0, new byte[] { 0x11 });
        StreamMessage.StaticCall child = new(0, TestItem.AddressB, TestItem.AddressC, 0, new byte[] { 0x22 });
        ValueHash256 rootHash = CrossChainCallHash.Compute(true, root.From, 0, root.To, 1, 0, 0, root.Data.Span);
        CrossChainCall callback = new(0, true, 0, child.From, 1, child.To, 0, child.Data.ToArray());
        StaticExecutionEntry entry = new([new ExpectedRoot(1, block.Hash!.ValueHash256)], rootHash, [callback],
            RollingHash.StaticResult(default, true, [0x45]), 1, true, [0x78]);
        RollupUpdate anchor = new(1, TestItem.KeccakA.ValueHash256, block.Hash.ValueHash256, SignedInt256.Zero);
        ExecutionEntry first = new([anchor], default, [], [], RollingHash.SeedL1(anchor, default), 1, true, []);
        PostBatch batch = new([], [first], [corrupt ? entry with { Calls = [callback with { Data = [0x23] }] } : entry],
            1, 0, [], [], [], [], [], 1, false);
        NativeStream stream = new(ObservationOperations(block),
            [new StreamMessage.Initiate(0, ReadOnlyMemory<byte>.Empty), root, child, Result(0x45), Result(0x78), new StreamMessage.Finish()]);

        if (corrupt)
            Assert.That(() => RollupXSettlementVerifier.Verify(batch, stream, [ObservationBlock(block)], 1), Throws.TypeOf<EezSettlementException>());
        else
        {
            RollupXObservationClaim claim = RollupXSettlementVerifier.Verify(batch, stream, [ObservationBlock(block)], 1)[0];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(claim.Mode, Is.EqualTo(ObservationMode.Static));
                Assert.That(claim.Callbacks, Has.Length.EqualTo(1));
                Assert.That((claim.Callbacks[0].Source, claim.Callbacks[0].Target), Is.EqualTo((child.From, child.To)));
                Assert.That(claim.Callbacks[0].Data, Is.EqualTo(new byte[] { 0x22 }));
                Assert.That(claim.Callbacks[0].Output, Is.EqualTo(new byte[] { 0x45 }));
            }
        }
    }

    private static EezStatelessBlockResult ObservationBlock(Block block) => new(block, block.Header,
        [new TxReceipt { StatusCode = StatusCode.Success, Logs = [] }],
        [new EezTransactionCheckpoint(0, TestItem.KeccakB, block.Hash!)]);

    private static byte[] ObservationOperations(Block block) => OperationsPayload.Encode(
        [new OperationsRun(1, block.Header.Beneficiary!, block.Header.ExtraData,
            [TxDecoder.Instance.Encode(block.Transactions[0], RlpBehaviors.SkipTypedWrapping).Bytes])]);
}
