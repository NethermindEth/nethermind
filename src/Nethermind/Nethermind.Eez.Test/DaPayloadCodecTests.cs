// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class DaPayloadCodecTests
{
    private const ulong RollupId = 1;
    private static readonly Address Beneficiary = new("0x1111111111111111111111111111111111111111");

    [Test]
    public void Decode_RecordedPayload_ReencodesToTheSameBytes()
    {
        byte[] payload = EezCalldata.DecodePostAndVerifyBatch(StatelessFixtures.ReadPostBatch(StatelessFixtures.Window84)).CallData;

        DaPayload decoded = DaPayloadCodec.Decode(payload);

        Assert.That(decoded.RollupId, Is.EqualTo(RollupId));
        Assert.That(decoded.Span.BlockCount, Is.EqualTo(6), "the recorded batch settles blocks 79 to 84");
        Assert.That(DaPayloadCodec.Encode(decoded.RollupId, Blocks(decoded.Span), decoded.Actions), Is.EqualTo(payload),
            "the codec writes what the composer wrote");
    }

    [Test]
    public void EncodeDecode_SpanAndActions_RoundTrip()
    {
        DaBlock[] blocks =
        [
            new DaBlock(Beneficiary, [], [[0x02, 0xf8, 0x6c]]),
            new DaBlock(Beneficiary, [], []),
            new DaBlock(Address.Zero, Enumerable.Repeat((byte)0xff, DaPayloadCodec.MaxExtraData).ToArray(), [new byte[200], [0x01]]),
        ];
        DaAction[] actions = [Action(true), Action(false)];

        DaPayload decoded = DaPayloadCodec.Decode(DaPayloadCodec.Encode(RollupId, blocks, actions));

        Assert.That(decoded.Span.TransactionCounts, Is.EqualTo(new[] { 1, 0, 2 }), "a zero count keeps an empty block in place");
        Assert.That(decoded.Span.Beneficiaries, Is.EqualTo(new[] { Beneficiary, Beneficiary, Address.Zero }));
        Assert.That(decoded.Span.ExtraData.Select(static e => e.ToArray()), Is.EqualTo(blocks.Select(static b => b.ExtraData)));
        Assert.That(decoded.Span.Transactions.Select(static t => t.ToArray()), Is.EqualTo(blocks.SelectMany(static b => b.Transactions)));
        Assert.That(decoded.Actions.Select(Describe), Is.EqualTo(actions.Select(Describe)));
    }

    [Test]
    public void Encode_RepeatedValues_CollapseIntoOneRun()
    {
        DaBlock[] blocks = Enumerable.Range(0, 64).Select(static _ => new DaBlock(Beneficiary, "eez"u8.ToArray(), [])).ToArray();

        byte[] encoded = DaPayloadCodec.Encode(RollupId, blocks, []);

        const int span = 1 + 1 + 64 + (1 + Address.Size) + (1 + 1 + 3);
        Assert.That(encoded, Has.Length.EqualTo(1 + 1 + sizeof(ulong) + 1 + span), "one beneficiary run and one extra data run");
    }

    [Test]
    public void Decode_PaddedStreamLength_IsAccepted()
    {
        byte[] canonical = DaPayloadCodec.Encode(RollupId, [new DaBlock(Beneficiary, [], [])], []);
        byte[] padded = [.. canonical[..10], (byte)(canonical[10] | 0x80), 0x00, .. canonical[11..]];

        Assert.That(DaPayloadCodec.Decode(padded).Span.BlockCount, Is.EqualTo(1), "stream byte lengths may use a padded varint");
    }

    [TestCaseSource(nameof(MalformedPayloads))]
    public void Decode_MalformedPayload_Throws(byte[] payload, string rule) =>
        Assert.That(Assert.Throws<EezSettlementException>(() => DaPayloadCodec.Decode(payload))!.Message, Does.Contain(rule));

    [TestCaseSource(nameof(NonCanonicalSpans))]
    public void Decode_NonCanonicalSpan_Throws(byte[] payload) =>
        Assert.That(Assert.Throws<EezSettlementException>(() => DaPayloadCodec.Decode(payload))!.Message, Does.Contain("padded varint").Or.Contain("repeat a value"));

    [Test]
    public void Encode_EmptySpan_Throws() =>
        Assert.Throws<EezSettlementException>(() => DaPayloadCodec.Encode(RollupId, [], []));

    [Test]
    public void Encode_ExtraDataTooLong_Throws() =>
        Assert.Throws<EezSettlementException>(() => DaPayloadCodec.Encode(RollupId, [new DaBlock(Beneficiary, new byte[DaPayloadCodec.MaxExtraData + 1], [])], []));

    [TestCase(0UL, RollupId, TestName = "Inbound")]
    [TestCase(RollupId, 0UL, TestName = "Outbound")]
    public void ActionEntry_RoundTrips(ulong source, ulong target)
    {
        DaAction action = Action(true) with { SourceRollupId = source, TargetRollupId = target, Gas = 0 };

        Assert.That(Describe(DaAction.FromEntry(action.ToEntry(RollupId), RollupId)), Is.EqualTo(Describe(action)));
    }

    [Test]
    public void ToEntry_Inbound_CommitsTheCallAndItsResult()
    {
        DaAction action = Action(true) with { Gas = 0 };

        ExecutionEntry entry = action.ToEntry(RollupId);

        Assert.That(entry.ProxyEntryHash, Is.EqualTo(CrossChainCallHash.Compute(false, action.SourceAddress, 0, action.TargetAddress, RollupId, action.Value, 0, action.Data)));
        Assert.That(entry.RollingHash, Is.EqualTo(RollingHash.CallEnd(RollingHash.CallBegin(RollingHash.SeedL2(entry.ProxyEntryHash), entry.ProxyEntryHash), true, action.ReturnData)));
    }

    [Test]
    public void ToEntry_ActionOfAnotherRollup_Throws() =>
        Assert.Throws<EezSettlementException>(() => (Action(true) with { SourceRollupId = 2, TargetRollupId = 3 }).ToEntry(RollupId));

    [Test]
    public void ToEntry_OutboundToAnotherRollup_Throws() =>
        Assert.Throws<EezSettlementException>(() => (Action(true) with { SourceRollupId = RollupId, TargetRollupId = 3 }).ToEntry(RollupId));

    [TestCaseSource(nameof(UnpublishableEntries))]
    public void FromEntry_EntryThatNoActionPublishes_Throws(Func<ExecutionEntry, ExecutionEntry> mutate) =>
        Assert.Throws<EezSettlementException>(() => DaAction.FromEntry(mutate(Action(true).ToEntry(RollupId)), RollupId));

    private static TestCaseData[] UnpublishableEntries() =>
    [
        new((Func<ExecutionEntry, ExecutionEntry>)(static e => e with { Calls = [] })) { TestName = "NoCall" },
        new((Func<ExecutionEntry, ExecutionEntry>)(static e => e with { Calls = [e.Calls[0], e.Calls[0]] })) { TestName = "TwoCalls" },
        new((Func<ExecutionEntry, ExecutionEntry>)(static e => e with { Calls = [e.Calls[0] with { IsStatic = true }] })) { TestName = "StaticCall" },
        new((Func<ExecutionEntry, ExecutionEntry>)(static e => e with { Calls = [e.Calls[0] with { RevertNextNCalls = 1 }] })) { TestName = "RevertSpan" },
        new((Func<ExecutionEntry, ExecutionEntry>)(static e => e with { ExpectedCalls = [new ExpectedCall(default, [], default, true, [])] })) { TestName = "ExpectedCalls" },
        new((Func<ExecutionEntry, ExecutionEntry>)(static e => e with { DestinationRollupId = RollupId + 1 })) { TestName = "InboundForAnotherRollup" },
    ];

    private static TestCaseData[] MalformedPayloads()
    {
        byte[] valid = DaPayloadCodec.Encode(RollupId, [new DaBlock(Beneficiary, [1], [[0x01, 0x02]])], [Action(true)]);
        return
        [
            new(Array.Empty<byte>(), "the payload is empty") { TestName = "Empty" },
            new(Set(valid, 0, 0x01), "stream version") { TestName = "UnknownStreamVersion" },
            new(Set(valid, 1, 0x03), "expected message type 2") { TestName = "NoChainOperation" },
            new(Stream([0x01]), "span version") { TestName = "UnknownSpanVersion" },
            new(Stream([0x00, 0x00]), "no blocks") { TestName = "NoBlocks" },
            new(Stream([0x00, 0x81, 0x00, 0x00, 0x01, .. new byte[20], 0x01, 0x00]), "padded varint") { TestName = "PaddedSpanVarint" },
            new(Stream([0x00, 0x02, 0x00, 0x00, 0x01, .. new byte[20], 0x01, .. new byte[20], 0x02, 0x00]), "repeat a value") { TestName = "NonMaximalRun" },
            new(Stream([0x00, 0x01, 0x00, 0x02, .. new byte[20], 0x01, 0x00]), "cover more than") { TestName = "RunOvershoots" },
            new(Stream([0x00, 0x01, 0x00, 0x00, .. new byte[20], 0x01, 0x00]), "run is empty") { TestName = "EmptyRun" },
            new(Stream([0x00, 0x01, 0x00, 0x01, .. new byte[20], 0x01, 33, .. new byte[33]]), "exceeds 32") { TestName = "ExtraDataTooLong" },
            new(Stream([0x00, 0x01, 0x01, 0x01, .. new byte[20], 0x01, 0x00, 0x00, 0x01]), "is empty") { TestName = "EmptyTransaction" },
            new(Stream([0x00, 0x01, 0x01, 0x01, .. new byte[20], 0x01, 0x00, 0x02, 0x01]), "declare 2 bytes") { TestName = "TransactionBytesShort" },
            new(Stream([0x00, 0x01, 0x01, 0x01, .. new byte[20], 0x01, 0x00, 0x01, 0x01, 0x02]), "declare 1 bytes") { TestName = "TransactionBytesLong" },
            new(Stream([0x00, 0xff, 0xff, 0xff, 0xff, 0x07]), "remaining bytes can encode") { TestName = "ImplausibleBlockCount" },
            new(Stream([0x00, 0xff, 0xff, 0xff, 0xff, 0x0f]), "out of range") { TestName = "VarintBeyondInt" },
            new(Stream([0x00, 0x01, 0xff, 0xff, 0xff, 0xff, 0x07, 0x01, .. new byte[20], 0x01, 0x00]), "transaction count") { TestName = "ImplausibleTransactionCount" },
            new(Set(valid, valid.Length - 4 - 1 - 4 - 1 - 8 - 32 - 20 - 20 - 8 - 1, 0x05), "expected message type 4") { TestName = "NoCallMessage" },
            new(Stream([0x00, 0x01, 0x00, 0x01, .. new byte[20], 0x01, 0x00], [0x05]), "expected message type 3") { TestName = "UnknownActionMessage" },
            new(Set(valid, valid.Length - 1, 0x09), "expected message type 10") { TestName = "UnfinishedAction" },
            new(valid[..^1], "truncated") { TestName = "TruncatedAction" },
            new(Set(valid, valid.Length - 4, 0x08), "unknown message type 8") { TestName = "UnknownReturnMessage" },
            new(Stream([0x00, 0x01, 0x00, 0x01, .. new byte[20], 0x01, 0x00], [], lengthPrefix: [0xff, 0xff, 0xff, 0xff, 0x1f]), "varint out of range") { TestName = "LengthVarintOutOfRange" },
        ];
    }

    private static TestCaseData[] NonCanonicalSpans() =>
    [
        new(Stream([0x00, 0x01, 0x81, 0x00, 0x01, .. new byte[20], 0x01, 0x00, 0x01, 0x01])) { TestName = "PaddedTransactionCount" },
        new(Stream([0x00, 0x01, 0x00, 0x81, 0x00, .. new byte[20], 0x01, 0x00])) { TestName = "PaddedRunLength" },
        new(Stream([0x00, 0x01, 0x01, 0x01, .. new byte[20], 0x01, 0x00, 0x81, 0x00, 0x01])) { TestName = "PaddedTransactionLength" },
        new(Stream([0x00, 0x02, 0x00, 0x00, 0x02, .. new byte[20], 0x01, 0x01, 0x07, 0x01, 0x01, 0x07])) { TestName = "NonMaximalExtraDataRun" },
    ];

    private static byte[] Stream(byte[] span, byte[]? tail = null, byte[]? lengthPrefix = null) =>
        [DaPayloadCodec.StreamVersion, 0x02, .. BitConverter.GetBytes(RollupId), .. lengthPrefix ?? [(byte)span.Length], .. span, .. tail ?? []];

    private static byte[] Set(byte[] bytes, int index, byte value)
    {
        byte[] copy = (byte[])bytes.Clone();
        copy[index] = value;
        return copy;
    }

    private static DaAction Action(bool success) => new(0, RollupId, new Address("0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
        new Address("0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), new UInt256(42, 0, 0, 1), 21_000, [0x51, 0xdd, 0x0a, 0xf6], success, [0x01]);

    private static string Describe(DaAction action) =>
        $"{action.SourceRollupId}|{action.TargetRollupId}|{action.SourceAddress}|{action.TargetAddress}|{action.Value}|{action.Gas}|{action.Data.ToHexString()}|{action.Success}|{action.ReturnData.ToHexString()}";

    private static DaBlock[] Blocks(DaSpan span)
    {
        DaBlock[] blocks = new DaBlock[span.BlockCount];
        int next = 0;
        for (int i = 0; i < blocks.Length; i++)
        {
            byte[][] transactions = span.Transactions[next..(next + span.TransactionCounts[i])].Select(static t => t.ToArray()).ToArray();
            next += span.TransactionCounts[i];
            blocks[i] = new DaBlock(span.Beneficiaries[i], span.ExtraData[i].ToArray(), transactions);
        }

        return blocks;
    }
}
