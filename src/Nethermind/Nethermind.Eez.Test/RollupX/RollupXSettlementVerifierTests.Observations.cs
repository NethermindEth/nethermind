// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Execution;
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
    public void Verify_RevertedSourceObservations_DerivesCompleteRollbackProgram([Values] bool multiple, [Values] bool native)
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
        NativeStream stream = new(ObservationOperations(block), messages.ToArray());

        if (!native && multiple)
        {
            Assert.That(() => VerifyObservation(batch, stream, block, native), Throws.TypeOf<EezSettlementException>());
            return;
        }
        RollupXObservationClaim[] claims = VerifyObservation(batch, stream, block, native);
        Assert.That(claims, Has.Length.EqualTo(multiple ? 2 : 1));
        for (int i = 0; i < claims.Length; i++)
        {
            StreamMessage.Call call = Call(1, 0, (byte)(i + 1));
            ValueHash256 hash = Hash(call, 0);
            CrossChainCall encoded = new(0, false, 0, call.From, 0, call.To, 0, call.Data.ToArray());
            L2ExecutionEntry entry = new(hash, [encoded], [], RollingHash.SingleL2Call(hash, true, new byte[] { 0x45 }), true, [0x45]);
            using (Assert.EnterMultipleScope())
            {
                if (native)
                {
                    Assert.That(claims[i].Delivery!.Data, Is.EqualTo(EezCalldata.EncodeExecuteIncomingCrossChainCall(new([entry], []))), "the scratch program is independently derived from each exact source action");
                    Assert.That(claims[i].RollbackSpans, Is.EqualTo(i == 0 ? new[] { claims.Length } : Array.Empty<int>()));
                }
                else
                {
                    Assert.That(claims[i].Delivery, Is.Null, "legacy witnesses retain direct target observation semantics");
                    Assert.That(claims[i].RollbackSpans, Is.Empty);
                }
                Assert.That(claims[i].Pin, Is.EqualTo(block.Hash));
            }
        }
    }

    [Test]
    public void Verify_StaticCallback_BindsCompleteEntryAndReturnsReplayClaim([Values] bool corrupt, [Values] bool callbackSuccess, [Values] bool native)
    {
        Block block = Build.A.Block.WithParentHash(TestItem.KeccakA).WithTransactions(User()).TestObject;
        StreamMessage.StaticCall root = new(1, TestItem.AddressC, TestItem.AddressB, 0, new byte[] { 0x11 });
        StreamMessage.StaticCall child = new(0, TestItem.AddressB, TestItem.AddressC, 0, new byte[] { 0x22 });
        ValueHash256 rootHash = CrossChainCallHash.Compute(true, root.From, 0, root.To, 1, 0, 0, root.Data.Span);
        CrossChainCall callback = new(0, true, 0, child.From, 1, child.To, 0, child.Data.ToArray());
        StaticExecutionEntry entry = new([new ExpectedRoot(1, block.Hash!.ValueHash256)], rootHash, [callback],
            RollingHash.StaticResult(default, callbackSuccess, [0x45]), 1, true, [0x78]);
        RollupUpdate anchor = new(1, TestItem.KeccakA.ValueHash256, block.Hash.ValueHash256, SignedInt256.Zero);
        ExecutionEntry first = new([anchor], default, [], [], RollingHash.SeedL1(anchor, default), 1, true, []);
        PostBatch batch = new([], [first], [corrupt ? entry with { Calls = [callback with { Data = [0x23] }] } : entry],
            1, 0, [], [], [], [], [], 1, false);
        NativeStream stream = new(ObservationOperations(block),
            [new StreamMessage.Initiate(0, ReadOnlyMemory<byte>.Empty), root, child, new StreamMessage.Return(callbackSuccess, new byte[] { 0x45 }), Result(0x78), new StreamMessage.Finish()]);

        if (corrupt || !native && !callbackSuccess)
            Assert.That(() => VerifyObservation(batch, stream, block, native), Throws.TypeOf<EezSettlementException>());
        else
        {
            RollupXObservationClaim claim = VerifyObservation(batch, stream, block, native)[0];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(claim.Mode, Is.EqualTo(ObservationMode.Static));
                Assert.That(claim.Callbacks, Has.Length.EqualTo(1));
                Assert.That((claim.Callbacks[0].Source, claim.Callbacks[0].Target), Is.EqualTo((child.From, child.To)));
                Assert.That(claim.Callbacks[0].Data, Is.EqualTo(new byte[] { 0x22 }));
                Assert.That(claim.Callbacks[0].Output, Is.EqualTo(new byte[] { 0x45 }));
                Assert.That(claim.Callbacks[0].Success, Is.EqualTo(callbackSuccess));
            }
        }
    }

    [Test]
    public void Verify_RecursiveStaticCallbacks_BindsEverySourceReadAndFailedCallback([Values] bool corruptDescendant, [Values] bool native)
    {
        Block block = Build.A.Block.WithParentHash(TestItem.KeccakA).WithTransactions(User()).TestObject;
        StreamMessage.StaticCall root = new(1, TestItem.AddressC, TestItem.AddressB, 0, new byte[] { 0x11 });
        StreamMessage.StaticCall child = new(0, TestItem.AddressB, TestItem.AddressC, 0, new byte[] { 0x22 });
        StreamMessage.StaticCall nested = new(1, TestItem.AddressC, TestItem.AddressB, 0, new byte[] { 0x33 });
        StreamMessage.StaticCall leaf = new(0, TestItem.AddressB, TestItem.AddressC, 0, new byte[] { 0x44 });
        ValueHash256 rootHash = CrossChainCallHash.Compute(true, root.From, 0, root.To, 1, 0, 0, root.Data.Span);
        ValueHash256 nestedHash = CrossChainCallHash.Compute(true, nested.From, 0, nested.To, 1, 0, 0, nested.Data.Span);
        ExpectedRoot[] pins = [new(1, block.Hash!.ValueHash256)];
        CrossChainCall firstCallback = new(0, true, 0, child.From, 1, child.To, 0, child.Data.ToArray());
        CrossChainCall nestedCallback = new(0, true, 0, leaf.From, 1, leaf.To, 0, leaf.Data.ToArray());
        StaticExecutionEntry first = new(pins, rootHash, [firstCallback], RollingHash.StaticResult(default, false, [0x45]), 1, true, [0x78]);
        StaticExecutionEntry second = new(pins, nestedHash, [nestedCallback], RollingHash.StaticResult(default, true, [0x66]), 1, true, [0x55]);
        RollupUpdate anchor = new(1, TestItem.KeccakA.ValueHash256, block.Hash.ValueHash256, SignedInt256.Zero);
        ExecutionEntry initial = new([anchor], default, [], [], RollingHash.SeedL1(anchor, default), 1, true, []);
        PostBatch batch = new([], [initial], [first, corruptDescendant ? second with { ReturnData = [0x56] } : second],
            1, 0, [], [], [], [], [], 1, false);
        NativeStream stream = new(ObservationOperations(block),
            [new StreamMessage.Initiate(0, ReadOnlyMemory<byte>.Empty), root, child, nested, leaf,
                Result(0x66), Result(0x55), new StreamMessage.Return(false, new byte[] { 0x45 }), Result(0x78), new StreamMessage.Finish()]);

        if (corruptDescendant || !native)
            Assert.That(() => VerifyObservation(batch, stream, block, native), Throws.TypeOf<EezSettlementException>());
        else
        {
            RollupXObservationClaim[] claims = VerifyObservation(batch, stream, block, native);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(claims, Has.Length.EqualTo(2), "every source-chain read must execute independently against the authenticated prefix");
                Assert.That(claims[0].Data, Is.EqualTo(root.Data.ToArray()));
                Assert.That(claims[0].Callbacks[0].Success, Is.False);
                Assert.That(claims[0].Callbacks[0].Output, Is.EqualTo(new byte[] { 0x45 }));
                Assert.That(claims[1].Data, Is.EqualTo(nested.Data.ToArray()));
                Assert.That(claims[1].Callbacks[0].Success, Is.True);
                Assert.That(claims[1].Callbacks[0].Output, Is.EqualTo(new byte[] { 0x66 }));
            }
        }
    }

    private static RollupXObservationClaim[] VerifyObservation(PostBatch batch, NativeStream stream, Block block, bool native)
    {
        EezChainRules? rules = native ? EezChainRules.NativeRollupX(new(2, 6, new(1, 0, TestItem.KeccakA, 0, TestItem.KeccakB), 0),
            TestItem.AddressD, NativeSystemTxFormat.ContractV7) : null;
        return RollupXSettlementVerifier.VerifyCore(batch, stream, [ObservationBlock(block)], 1, default, rules, OperationsPayload.Decode(stream.Operations));
    }

    private static EezStatelessBlockResult ObservationBlock(Block block) => new(block, block.Header,
        [new TxReceipt { StatusCode = StatusCode.Success, Logs = [] }],
        [new EezTransactionCheckpoint(0, TestItem.KeccakB, block.Hash!)]);

    private static byte[] ObservationOperations(Block block) => OperationsPayload.Encode(
        [new OperationsRun(1, block.Header.Beneficiary!, block.Header.ExtraData,
            [TxDecoder.Instance.Encode(block.Transactions[0], RlpBehaviors.SkipTypedWrapping).Bytes])]);
}
