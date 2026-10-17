// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Eez.Test.RollupX;

public class RollupXBatchDerivationTests
{
    private const ulong RollupId = 1;
    private const ulong ChainId = 6290;
    private const ulong StartingNonce = 7;
    private static readonly EezSettlementContext Context = new(RollupId, ChainId, Address.Zero, default, 2);
    private static readonly EezChainRules Rules = EezChainRules.RollupX(new RollupXSchedule(2, 6,
        new RollupXL1Reference(1, 0, TestItem.KeccakA, 0, TestItem.KeccakB), 0));
    private static readonly StreamMessage.Call Incoming = new(RollupId, TestItem.AddressA, TestItem.AddressB, 3, 0, new byte[] { 0x11, 0x22 });
    private static readonly StreamMessage.Return Success = new(true, new byte[] { 0xab });

    [TestCase(2, TestName = "Derive_TwoIdenticalInboundCalls_PreservesBothDeliveries")]
    [TestCase(3, TestName = "Derive_ThreeIdenticalInboundCalls_PreservesAllDeliveries")]
    public void Derive_RepeatedInboundCalls_PreservesOrderAndNonces(int count)
    {
        List<StreamMessage> messages = [new StreamMessage.Initiate(0, ReadOnlyMemory<byte>.Empty)];
        for (int i = 0; i < count; i++)
        {
            messages.Add(Incoming);
            messages.Add(Success);
        }
        messages.Add(new StreamMessage.Finish());

        DerivedBlock[] blocks = Derive(messages.ToArray());

        Assert.That(blocks, Has.Length.EqualTo(6), "the full Sync interval must remain present");
        Assert.That(blocks[^1].Transactions, Has.Length.EqualTo(count), "identical calls are distinct canonical deliveries");
        for (int i = 0; i < count; i++)
        {
            AssertDelivery(blocks[^1].Transactions[i], StartingNonce + (ulong)i, Incoming, Success);
        }
    }

    [Test]
    public void Derive_SeparateInboundTransactions_KeepsResultsAndNoncesInOrder()
    {
        StreamMessage.Call other = Incoming with { To = TestItem.AddressC, Value = 5, Data = new byte[] { 0x33 } };
        StreamMessage.Return otherResult = Success with { ReturnData = new byte[] { 0xcd } };
        StreamMessage[] messages = [
            .. Inbound(Incoming, Success, Incoming, otherResult),
            .. Inbound(other, Success),
        ];

        byte[][] transactions = Derive(messages)[^1].Transactions;

        Assert.That(transactions, Has.Length.EqualTo(3), "source transaction boundaries must not drop or repeat a delivery");
        AssertDelivery(transactions[0], StartingNonce, Incoming, Success);
        AssertDelivery(transactions[1], StartingNonce + 1, Incoming, otherResult);
        AssertDelivery(transactions[2], StartingNonce + 2, other, Success);
    }

    [Test]
    public void Derive_SelectedRepeatedInboundEffect_AppendsOnlyTheSelectedCall()
    {
        StreamMessage.Return otherResult = Success with { ReturnData = new byte[] { 0xcd } };

        DerivedBlock[] blocks = Derive(Inbound(Incoming, Success, Incoming, otherResult), new ProducingSlice(1, 1), resumed: true);

        Assert.That(blocks, Has.Length.EqualTo(1), "resumption rebuilds only the terminal Sync block");
        Assert.That(blocks[0].Transactions, Has.Length.EqualTo(1), "a producing slice must not replay its skipped delivery");
        AssertDelivery(blocks[0].Transactions[0], StartingNonce + 1, Incoming, otherResult);
    }

    [Test]
    public void Derive_FailedInboundLookupBetweenCalls_EmitsOnlySuccessfulDeliveries()
    {
        StreamMessage.Return lookup = new(false, new byte[] { 0xef });

        byte[][] transactions = Derive(Inbound(Incoming, Success, Incoming, lookup, Incoming, Success))[^1].Transactions;

        Assert.That(transactions, Has.Length.EqualTo(2), "a failed inbound lookup must not become an executing system transaction");
        AssertDelivery(transactions[0], StartingNonce, Incoming, Success);
        AssertDelivery(transactions[1], StartingNonce + 1, Incoming, Success);
    }

    [Test]
    public void Decode_OutboundAfterFailedInboundLookup_RejectsInvalidOrder()
    {
        StreamMessage[] messages = [
            .. Inbound(Incoming, new StreamMessage.Return(false, ReadOnlyMemory<byte>.Empty)),
            .. Outbound(Incoming with { ToChain = 0 }, Success),
        ];

        Assert.That(() => Decode(messages), Throws.TypeOf<EezSettlementException>(),
            "an inbound lookup still establishes the inbound phase even without an executing delivery");
    }

    [Test]
    public void Derive_MultipleOutboundCalls_LoadsOneTableAndExecutesTheUserOnce()
    {
        StreamMessage.Call outgoing = Incoming with { ToChain = 0 };

        byte[][] transactions = Derive(Outbound(outgoing, Success, outgoing, Success))[^1].Transactions;
        ValueHash256 hash = CallHash(outgoing, RollupId);
        L2ExecutionEntry entry = new(hash, [], [], RollingHash.SeedL2(hash), true, Success.ReturnData.ToArray());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(transactions, Has.Length.EqualTo(2), "all source roots share one load and one signed user transaction");
            Assert.That(DecodeTransaction(transactions[0]).Data.ToArray(),
                Is.EqualTo(EezCalldata.EncodeLoadExecutionTable(new ExecutionTable([entry, entry], []))), "equal calls occupy separate source cursor slots");
            Assert.That(transactions[1], Is.EqualTo(((StreamMessage.Initiate)Outbound()[0]).TxData.ToArray()), "the signed user executes exactly once");
        }
    }

    [Test]
    public void Derive_ResumedOutboundGroup_SkipsGroupsRatherThanSourceRoots()
    {
        StreamMessage.Call outgoing = Incoming with { ToChain = 0 };
        StreamMessage[] messages = [.. Outbound(outgoing, Success, outgoing, Success), .. Outbound(outgoing, Success), .. Inbound(Incoming, Success)];

        byte[][] transactions = Derive(messages, new ProducingSlice(1, 2), resumed: true)[^1].Transactions;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(transactions, Has.Length.EqualTo(3), "resumption selects a whole outbound group and one delivery");
            Assert.That(DecodeTransaction(transactions[0]).Nonce, Is.EqualTo(StartingNonce + 1), "a skipped group consumes one system nonce, regardless of root count");
            Assert.That(DecodeTransaction(transactions[2]).Nonce, Is.EqualTo(StartingNonce + 2), "inbound nonce follows the selected group load");
        }
    }

    [Test]
    public void Derive_RevertedSourceRootRetry_ReusesOneSourceSlot()
    {
        StreamMessage.Call outgoing = Incoming with { ToChain = 0 };
        StreamMessage[] messages = Outbound(new StreamMessage.Snapshot(), outgoing, Success, new StreamMessage.Revert(), outgoing, Success);

        byte[][] transactions = Derive(messages)[^1].Transactions;
        ValueHash256 hash = CallHash(outgoing, RollupId);
        L2ExecutionEntry entry = new(hash, [], [], RollingHash.SeedL2(hash), true, Success.ReturnData.ToArray());

        Assert.That(DecodeTransaction(transactions[0]).Data.ToArray(),
            Is.EqualTo(EezCalldata.EncodeLoadExecutionTable(new ExecutionTable([entry], []))), "rollback restores the source entry cursor before the retry");
    }

    [TestCase(0, TestName = "Decode_SourceSlotRetryWithDifferentIdentity_Rejects")]
    [TestCase(1, TestName = "Decode_SourceSlotRetryWithDifferentSuccess_Rejects")]
    [TestCase(2, TestName = "Decode_SourceSlotRetryWithDifferentOutput_Rejects")]
    public void Decode_SourceSlotRetryWithDifferentCallOrResult_Rejects(int changed)
    {
        StreamMessage.Call outgoing = Incoming with { ToChain = 0 };
        StreamMessage.Call retry = changed == 0 ? outgoing with { To = TestItem.AddressC } : outgoing;
        StreamMessage.Return result = changed switch
        {
            1 => Success with { Success = false },
            2 => Success with { ReturnData = new byte[] { 0xcc } },
            _ => Success,
        };
        StreamMessage[] messages = Outbound(new StreamMessage.Snapshot(), outgoing, Success, new StreamMessage.Revert(), retry, result);

        Assert.That(() => Decode(messages), Throws.TypeOf<EezSettlementException>().With.Property(nameof(EezSettlementException.Failure))
            .EqualTo(EezSettlementFailure.InvalidDaPayload), "source cursor reuse must retain the same call identity and result");
    }

    [TestCase(false, false, TestName = "Derive_FailedNestedCall_RestoresCallerHash")]
    [TestCase(true, false, TestName = "Derive_SuccessfulNestedCall_FoldsNestedEnd")]
    [TestCase(true, true, TestName = "Derive_DeepCallback_FundsEveryIncomingArray")]
    public void Derive_NestedCalls_ReconstructsRowsAndRollingHashes(bool success, bool callback)
    {
        StreamMessage.Call outgoing = Incoming with { ToChain = 0, From = TestItem.AddressC, Value = 5, Data = new byte[] { 0x44 } };
        StreamMessage.Call returning = Incoming with { From = TestItem.AddressD, Value = 7, Data = new byte[] { 0x55 } };
        StreamMessage.Return nestedResult = new(success, new byte[] { 0xdd });
        StreamMessage[] messages = Inbound([Incoming, outgoing,
            .. callback ? new StreamMessage[] { returning, Success } : [], nestedResult, Success]);

        Transaction transaction = DecodeTransaction(Derive(messages)[^1].Transactions[0]);

        ValueHash256 incomingHash = CallHash(Incoming, 0);
        ValueHash256 outgoingHash = CallHash(outgoing, RollupId);
        ValueHash256 open = RollingHash.CallBegin(RollingHash.SeedL2(incomingHash), incomingHash);
        ValueHash256 nested = RollingHash.NestedBegin(open, outgoingHash);
        if (callback) nested = RollingHash.CallEnd(RollingHash.CallBegin(nested, CallHash(returning, 0)), true, Success.ReturnData.Span);
        ValueHash256 closed = success ? RollingHash.NestedEnd(nested) : open;
        ExpectedCall row = new(Position(outgoingHash, open), callback ? [Encoded(returning, 0)] : [], success ? default : nested, success, nestedResult.ReturnData.ToArray());
        L2ExecutionEntry entry = new(incomingHash, [Encoded(Incoming, 0)], [row], RollingHash.CallEnd(closed, true, Success.ReturnData.Span), true, Success.ReturnData.ToArray());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(transaction.Data.ToArray(), Is.EqualTo(EezCalldata.EncodeExecuteIncomingCrossChainCall(new IncomingCrossChainCall([entry], []))),
                "nested position, tentative failure hash, callback array and final rolling hash must match the execution trace");
            Assert.That(transaction.Value, Is.EqualTo((UInt256)(callback ? 10 : 3)), "funding sums incoming calls across root and nested row arrays");
        }
    }

    [Test]
    public void Derive_NestedCommittedValueOverflow_Rejects()
    {
        StreamMessage[] messages = Inbound(Incoming with { Value = UInt256.MaxValue }, Incoming with { ToChain = 0 },
            Incoming with { Value = 1 }, Success, Success, Success);

        Assert.That(() => Derive(messages), Throws.TypeOf<EezSettlementException>(), "all incoming arrays contribute to checked uint256 funding");
    }

    [Test]
    public void Decode_RevertedRowRetryWithDifferentResult_Rejects()
    {
        StreamMessage.Call outgoing = Incoming with { ToChain = 0 };
        StreamMessage[] messages = Inbound(Incoming, new StreamMessage.Snapshot(), outgoing, Success, new StreamMessage.Revert(),
            outgoing, Success with { ReturnData = new byte[] { 0xdd } }, Success);

        Assert.That(() => Decode(messages), Throws.TypeOf<EezSettlementException>(), "restored row cursors cannot answer a retry with a different result");
    }

    [Test]
    public void Derive_StaticAndRolledBackInboundRoots_ProduceNoDelivery()
    {
        StreamMessage[] messages = Inbound(new StreamMessage.StaticCall(RollupId, Incoming.From, Incoming.To, 0, Incoming.Data), Success,
            new StreamMessage.Snapshot(), Incoming, Success, new StreamMessage.Revert());

        Assert.That(Derive(messages)[^1].Transactions, Is.Empty, "read-only lookups and source-reverted roots authorize no L2 delivery");
    }

    [TestCase(ulong.MaxValue, 0, TestName = "Derive_SystemNonceAtMaximum_Rejects")]
    [TestCase(ulong.MaxValue, 1, TestName = "Derive_SkippedSystemNonceOverflow_Rejects")]
    [TestCase(ulong.MaxValue - 1, 0, TestName = "Derive_SystemNonceOverflowsDuringEmission_Rejects")]
    public void Derive_SystemNonceOverflow_Rejects(ulong nonce, int skip) =>
        Assert.That(() => Derive(Inbound(Incoming, Success, Incoming, Success), new ProducingSlice(skip, 2), nonce: nonce),
            Throws.TypeOf<EezSettlementException>().With.Property(nameof(EezSettlementException.Failure)).EqualTo(EezSettlementFailure.InvalidDaPayload),
            "native nonces must not wrap during skip or transaction emission");

    [TestCaseSource(nameof(LiveBatches))]
    public void Derive_RecordedNativeBatch_MatchesCanonicalSystemCalldata(string name)
    {
        string directory = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "rollupx", "native-live");
        using JsonDocument fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, name + ".json")));
        JsonElement recorded = fixture.RootElement;
        byte[] physical = File.ReadAllBytes(Path.Combine(directory, name + ".blob"));
        Assert.That(Convert.ToHexStringLower(SHA256.HashData(physical)), Is.EqualTo(recorded.GetProperty("BlobSha256").GetString()),
            "the regression must replay the exact physical blobs captured from accepted L1 DA");
        List<byte[]> blobs = [];
        for (int offset = 0; offset < physical.Length; offset += BlobStream.BytesPerBlob) blobs.Add(physical.AsSpan(offset, BlobStream.BytesPerBlob).ToArray());
        EezSettlementContext context = Context with { ChainId = 696990 };
        DecodedBatch batch = new RollupXBatchDerivation(context, Rules).Decode(BlobStream.Unpack(blobs));
        BlockHeader parent = Build.A.BlockHeader.WithNumber(0).WithTimestamp(0).TestObject;
        DerivationInputs inputs = new(StartingNonce, TestItem.KeccakB, parent,
            new L1Anchor(1, TestItem.KeccakA, batch.BlockCount * 2, TestItem.KeccakB), false);

        byte[][] transactions = batch.Derive(new ProducingSlice(0, int.MaxValue), inputs)[^1].Transactions;
        Transaction[] decoded = transactions.Select(DecodeTransaction).ToArray();
        JsonElement.ArrayEnumerator expected = recorded.GetProperty("SystemTransactions").EnumerateArray();
        Assert.That(decoded.Count(static tx => tx.Type == EezConstants.SystemTxType), Is.EqualTo(expected.Count()),
            "canonical reconstruction must preserve every system transaction without adding synthetic deliveries");
        ulong nonce = StartingNonce;
        foreach (JsonElement system in expected)
        {
            Transaction actual = decoded[system.GetProperty("Index").GetInt32()];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(actual.Type, Is.EqualTo(EezConstants.SystemTxType), "the original L2 transaction position must contain the native envelope");
                Assert.That(actual.Nonce, Is.EqualTo(nonce++), "native nonces must remain consecutive across grouped outbound and inbound effects");
                Assert.That(actual.Data.ToArray(), Is.EqualTo(Bytes.FromHexString(system.GetProperty("Input").GetString()!)),
                    "the complete nested/static/rollback ABI must equal independently captured canonical L2 calldata");
            }
        }
        byte[][] users = recorded.GetProperty("UserTransactions").EnumerateArray().Select(static tx => Bytes.FromHexString(tx.GetString()!)).ToArray();
        foreach (byte[] user in users) Assert.That(transactions.Count(raw => raw.AsSpan().SequenceEqual(user)), Is.EqualTo(1), "each original signed source transaction executes once");
    }

    [TestCaseSource(nameof(LiveBatches))]
    public void Materialize_RecordedNativeBatch_MatchesCompleteCanonicalL1Tables(string name)
    {
        string directory = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "rollupx", "native-live");
        using JsonDocument fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, name + ".json")));
        JsonElement recorded = fixture.RootElement;
        byte[] posting = Bytes.FromHexString(recorded.GetProperty("PostBatchCalldata").GetString()!);
        Assert.That(Convert.ToHexStringLower(SHA256.HashData(posting)), Is.EqualTo(recorded.GetProperty("PostBatchSha256").GetString()),
            "the independently captured L1 posting retains its exact ABI and original proof bytes");
        PostBatch batch = EezCalldata.DecodePostAndVerifyBatch(posting);
        byte[] physical = File.ReadAllBytes(Path.Combine(directory, name + ".blob"));
        Assert.That(Convert.ToHexStringLower(SHA256.HashData(physical)), Is.EqualTo(recorded.GetProperty("BlobSha256").GetString()),
            "the L1 posting oracle and original physical blob must stay independent of the materializer");
        List<byte[]> blobs = [];
        for (int offset = 0; offset < physical.Length; offset += BlobStream.BytesPerBlob) blobs.Add(physical.AsSpan(offset, BlobStream.BytesPerBlob).ToArray());
        NativeStream stream = NativeStream.Decode(BlobStream.Unpack(blobs), RollupId);
        RollupUpdate[] updates = batch.Entries.Select(static entry => entry.RollupUpdates.Single()).ToArray();
        NativeL1Tables l1 = new(updates.AsMemory(1), RollupId, updates[0].NewRoot);
        int index = 0;
        while (index < stream.Messages.Length)
        {
            StreamMessage.Initiate start = (StreamMessage.Initiate)stream.Messages[index++];
            NativeCallTree tree = NativeCallTree.Read(stream.Messages, ref index, start.ChainId, RollupId);
            NativeExecutionTables tables = new(tree, RollupId, l1);
            if (start.ChainId == RollupId) tables.Outbound();
            else tables.Inbound();
        }
        l1.Complete();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(l1.Entries.Select(EezCalldata.EntryHash), Is.EqualTo(batch.Entries.Skip(1).Select(EezCalldata.EntryHash)),
                "every L1 field, nested position, rollback marker, value, hash and outcome must equal the original accepted posting");
            Assert.That(l1.StaticEntries.Select(EezCalldata.StaticEntryHash), Is.EqualTo(batch.StaticEntries.Select(EezCalldata.StaticEntryHash)),
                "static pins, callback arrays and result folds must equal the independent L1 oracle");
        }
    }

    private static IEnumerable<string> LiveBatches() =>
        ["a9eaf4a03ff196d8", "4f9482cb1caa8968", "ff1f5d1231f68909", "8a24e36aee6b2790",
            "7f37548c16e3ab0c", "ae7f4b9f1b5dcebc", "d1b5ba7cf7f0de3d", "9637ee00bfd583dc"];

    private static Transaction DecodeTransaction(byte[] encoded) => TxDecoder.Instance.Decode(encoded, RlpBehaviors.SkipTypedWrapping)!;

    private static ValueHash256 CallHash(StreamMessage.Call call, ulong source) =>
        CrossChainCallHash.Compute(false, call.From, source, call.To, call.ToChain, call.Value, 0, call.Data.Span);

    private static CrossChainCall Encoded(StreamMessage.Call call, ulong source) => new(0, false, 0, call.From, source, call.To, call.Value, call.Data.ToArray());

    private static ValueHash256 Position(in ValueHash256 call, in ValueHash256 hash)
    {
        Span<byte> bytes = stackalloc byte[64];
        call.Bytes.CopyTo(bytes);
        hash.Bytes.CopyTo(bytes[32..]);
        return ValueKeccak.Compute(bytes);
    }

    [TestCaseSource(nameof(UnsupportedInbound))]
    public void Decode_UnsupportedInboundShape_Rejects(StreamMessage[] calls) =>
        Assert.That(() => Decode(Inbound(calls)), Throws.TypeOf<EezSettlementException>(),
            "accepting sequential flat calls must not admit unsupported semantics");

    private static DecodedBatch Decode(StreamMessage[] messages)
    {
        byte[] operations = OperationsPayload.Encode([new OperationsRun(6, Address.Zero, ReadOnlyMemory<byte>.Empty, [])]);
        return new RollupXBatchDerivation(Context, Rules).Decode(NativeStream.Encode(RollupId, operations, messages));
    }

    private static DerivedBlock[] Derive(StreamMessage[] messages, ProducingSlice? applied = null, bool resumed = false, ulong nonce = StartingNonce)
    {
        BlockHeader parent = Build.A.BlockHeader.WithNumber(0).WithTimestamp(0).TestObject;
        DerivationInputs inputs = new(nonce, TestItem.KeccakB, parent, new L1Anchor(1, TestItem.KeccakA, 12, TestItem.KeccakB), resumed);
        return Decode(messages).Derive(applied ?? new ProducingSlice(0, int.MaxValue), inputs);
    }

    private static void AssertDelivery(byte[] encoded, ulong nonce, StreamMessage.Call call, StreamMessage.Return result)
    {
        Transaction transaction = TxDecoder.Instance.Decode(encoded, RlpBehaviors.SkipTypedWrapping)!;
        ValueHash256 hash = CrossChainCallHash.Compute(false, call.From, 0, call.To, RollupId, call.Value, 0, call.Data.Span);
        CrossChainCall incoming = new(0, false, 0, call.From, 0, call.To, call.Value, call.Data.ToArray());
        L2ExecutionEntry entry = new(hash, [incoming], [], RollingHash.SingleL2Call(hash, true, result.ReturnData.Span), true, result.ReturnData.ToArray());
        byte[] expected = EezCalldata.EncodeExecuteIncomingCrossChainCall(new IncomingCrossChainCall([entry], []));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(transaction.Type, Is.EqualTo(EezConstants.SystemTxType), "each inbound effect is an unsigned native transaction");
            Assert.That(transaction.ChainId, Is.EqualTo(ChainId), "the native transaction must retain its L2 chain identity");
            Assert.That(transaction.To, Is.EqualTo(EezConstants.RollupXEezl2Address), "RollupX deliveries target only the native manager");
            Assert.That(transaction.Nonce, Is.EqualTo(nonce), "each delivery consumes the next system nonce without resetting at a source boundary");
            Assert.That(transaction.Value, Is.EqualTo(call.Value), "each delivery funds its own committed incoming call");
            Assert.That(transaction.Data.ToArray(), Is.EqualTo(expected), "call identity and output must bind to this delivery in order");
        }
    }

    private static StreamMessage[] Inbound(params StreamMessage[] calls) =>
        [new StreamMessage.Initiate(0, ReadOnlyMemory<byte>.Empty), .. calls, new StreamMessage.Finish()];

    private static StreamMessage[] Outbound(params StreamMessage[] calls)
    {
        byte[] user = TxDecoder.Instance.Encode(Build.A.Transaction.WithType(TxType.EIP1559).WithChainId(ChainId)
            .WithSignature(new Signature(1, 2, 27)).TestObject, RlpBehaviors.SkipTypedWrapping).Bytes;
        return [new StreamMessage.Initiate(RollupId, user), .. calls, new StreamMessage.Finish()];
    }

    private static IEnumerable<TestCaseData> UnsupportedInbound()
    {
        yield return new TestCaseData(new object[] { Array.Empty<StreamMessage>() }).SetName("Decode_EmptyInboundBracket_Rejects");
        yield return new TestCaseData(new object[] { new StreamMessage[] { Incoming, Incoming, Success, Success } }).SetName("Decode_SameChainNestedCall_Rejects");
        yield return new TestCaseData(new object[] { new StreamMessage[] { Incoming, Incoming with { ToChain = 0 }, Success,
            new StreamMessage.Return(false, ReadOnlyMemory<byte>.Empty) } }).SetName("Decode_FailedCallWithDescendants_Rejects");
        yield return new TestCaseData(new object[] { new StreamMessage[] {
            new StreamMessage.StaticCall(RollupId, Incoming.From, Incoming.To, 0, Incoming.Data), Incoming with { ToChain = 0 }, Success, Success,
        } }).SetName("Decode_MutableCallbackInStaticRoot_Rejects");
        yield return new TestCaseData(new object[] { new StreamMessage[] {
            new StreamMessage.StaticCall(RollupId, Incoming.From, Incoming.To, 0, Incoming.Data),
            new StreamMessage.StaticCall(0, Incoming.To, Incoming.From, 0, Incoming.Data), new StreamMessage.Return(false, ReadOnlyMemory<byte>.Empty), Success,
        } }).SetName("Decode_FailedStaticCallback_Rejects");
        yield return new TestCaseData(new object[] { new StreamMessage[] {
            Incoming, new StreamMessage.StaticCall(0, Incoming.From, Incoming.To, 0, Incoming.Data),
            new StreamMessage.StaticCall(RollupId, Incoming.To, Incoming.From, 0, Incoming.Data), Success, Success, Success,
        } }).SetName("Decode_NestedStaticCallWithDescendants_Rejects");
        yield return new TestCaseData(new object[] { new StreamMessage[] {
            new StreamMessage.Snapshot(), new StreamMessage.StaticCall(RollupId, Incoming.From, Incoming.To, 0, Incoming.Data), Success, new StreamMessage.Revert(),
        } }).SetName("Decode_StaticRootStartingRevertRegion_Rejects");
        yield return new TestCaseData(new object[] { new StreamMessage[] { Incoming with { Gas = 1 }, Success } }).SetName("Decode_InboundCallWithGas_Rejects");
        yield return new TestCaseData(new object[] { new StreamMessage[] { Incoming with { ToChain = 2 }, Success } }).SetName("Decode_InboundCallToOtherRollup_Rejects");
    }
}
