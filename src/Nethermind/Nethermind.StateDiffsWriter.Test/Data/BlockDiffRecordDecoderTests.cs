// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.StateDiff.Core.Data;
using Nethermind.StateDiffsWriter.Data;
using NUnit.Framework;

namespace Nethermind.StateDiffsWriter.Test.Data;

[TestFixture]
public class BlockDiffRecordDecoderTests
{
    private static byte[] Encode(BlockDiffRecord record)
    {
        int length = BlockDiffRecordDecoder.Instance.GetLength(record);
        byte[] buffer = new byte[length];
        RlpWriter writer = new(buffer);
        BlockDiffRecordDecoder.Instance.Encode(ref writer, record);
        return buffer;
    }

    private static BlockDiffRecord Decode(byte[] bytes)
    {
        RlpReader ctx = new(bytes);
        return BlockDiffRecordDecoder.Instance.Decode(ref ctx)
            ?? throw new AssertionException("Encoded BlockDiffRecord decoded to null.");
    }

    [Test]
    public void EmptyRecord_RoundTripsExactly()
    {
        BlockDiffRecord original = new(
            BlockNumber: 1234,
            StateRoot: TestItem.KeccakA,
            CodeHashChanges: [],
            SlotCountChanges: []);

        byte[] bytes = Encode(original);
        BlockDiffRecord roundTripped = Decode(bytes);

        Assert.That(roundTripped.BlockNumber, Is.EqualTo(original.BlockNumber));
        Assert.That(roundTripped.StateRoot, Is.EqualTo(original.StateRoot));
        Assert.That(roundTripped.CodeHashChanges, Is.Empty);
        Assert.That(roundTripped.SlotCountChanges, Is.Empty);
    }

    [Test]
    public void GainedCode_NoCodeSentinelEncodedAsEmptyByteString()
    {
        CodeHashEntry gain = new(CodeHashChange.NoCode, TestItem.KeccakB.ValueHash256, NewCodeSize: 24);
        BlockDiffRecord original = new(
            BlockNumber: 42,
            StateRoot: TestItem.KeccakC,
            CodeHashChanges: [gain],
            SlotCountChanges: []);

        byte[] bytes = Encode(original);
        BlockDiffRecord roundTripped = Decode(bytes);

        Assert.That(roundTripped.CodeHashChanges, Has.Count.EqualTo(1));
        Assert.That(roundTripped.CodeHashChanges[0].OldHash, Is.EqualTo(CodeHashChange.NoCode));
        Assert.That(roundTripped.CodeHashChanges[0].NewHash, Is.EqualTo(TestItem.KeccakB.ValueHash256));
        Assert.That(roundTripped.CodeHashChanges[0].NewCodeSize, Is.EqualTo(24u));
    }

    [Test]
    public void LostCode_NoCodeSentinelEncodedAsEmptyByteString()
    {
        CodeHashEntry loss = new(TestItem.KeccakA.ValueHash256, CodeHashChange.NoCode, NewCodeSize: 0);
        BlockDiffRecord original = new(
            BlockNumber: 7,
            StateRoot: TestItem.KeccakC,
            CodeHashChanges: [loss],
            SlotCountChanges: []);

        BlockDiffRecord roundTripped = Decode(Encode(original));

        Assert.That(roundTripped.CodeHashChanges[0].OldHash, Is.EqualTo(TestItem.KeccakA.ValueHash256));
        Assert.That(roundTripped.CodeHashChanges[0].NewHash, Is.EqualTo(CodeHashChange.NoCode));
        Assert.That(roundTripped.CodeHashChanges[0].NewCodeSize, Is.Zero);
    }

    [Test]
    public void SlotCountEntries_RoundTripCounts()
    {
        SlotCountEntry a = new(TestItem.KeccakA.ValueHash256, OldCount: 0, NewCount: 5);
        SlotCountEntry b = new(TestItem.KeccakB.ValueHash256, OldCount: 1_000_000_000UL, NewCount: 999_999_999UL);
        BlockDiffRecord original = new(
            BlockNumber: 1,
            StateRoot: TestItem.KeccakC,
            CodeHashChanges: [],
            SlotCountChanges: [a, b]);

        BlockDiffRecord roundTripped = Decode(Encode(original));

        Assert.That(roundTripped.SlotCountChanges, Has.Count.EqualTo(2));
        Assert.That(roundTripped.SlotCountChanges[0], Is.EqualTo(a));
        Assert.That(roundTripped.SlotCountChanges[1], Is.EqualTo(b));
    }

    [Test]
    public void MixedRecord_PreservesOrderAndContent()
    {
        List<CodeHashEntry> codeChanges =
        [
            new(CodeHashChange.NoCode, TestItem.KeccakA.ValueHash256, 12),
            new(TestItem.KeccakA.ValueHash256, TestItem.KeccakB.ValueHash256, 999),
            new(TestItem.KeccakB.ValueHash256, CodeHashChange.NoCode, 0),
        ];
        List<SlotCountEntry> slotChanges =
        [
            new(TestItem.KeccakC.ValueHash256, 3, 7),
            new(TestItem.KeccakD.ValueHash256, 100, 0),
        ];
        BlockDiffRecord original = new(
            BlockNumber: 987_654_321,
            StateRoot: TestItem.KeccakE,
            CodeHashChanges: codeChanges,
            SlotCountChanges: slotChanges);

        BlockDiffRecord roundTripped = Decode(Encode(original));

        Assert.That(roundTripped.BlockNumber, Is.EqualTo(original.BlockNumber));
        Assert.That(roundTripped.StateRoot, Is.EqualTo(original.StateRoot));
        Assert.That(roundTripped.CodeHashChanges, Is.EqualTo(codeChanges));
        Assert.That(roundTripped.SlotCountChanges, Is.EqualTo(slotChanges));
    }

    [Test]
    public void DiagnosticRoundTrip_PrintsHexAndIsByteIdentical()
    {
        BlockDiffRecord original = new(
            BlockNumber: 9_999_001,
            StateRoot: TestItem.KeccakA,
            CodeHashChanges:
            [
                new CodeHashEntry(CodeHashChange.NoCode, TestItem.KeccakB.ValueHash256, NewCodeSize: 1024),
            ],
            SlotCountChanges:
            [
                new SlotCountEntry(TestItem.KeccakC.ValueHash256, OldCount: 0, NewCount: 17),
            ]);

        byte[] first = Encode(original);
        BlockDiffRecord decoded = Decode(first);
        byte[] second = Encode(decoded);

        Console.WriteLine("Encoded BlockDiffRecord (hex): " + Convert.ToHexString(first));
        Console.WriteLine("Decoded BlockNumber: " + decoded.BlockNumber);
        Console.WriteLine("Decoded StateRoot:   " + decoded.StateRoot);
        Console.WriteLine("Decoded CodeChanges: " + decoded.CodeHashChanges.Count);
        Console.WriteLine("Decoded SlotChanges: " + decoded.SlotCountChanges.Count);

        Assert.That(second, Is.EqualTo(first), "encode → decode → encode must produce identical bytes");
    }

    [Test]
    public void EncodedLength_MatchesEncodedBytes()
    {
        BlockDiffRecord record = new(
            BlockNumber: 555,
            StateRoot: TestItem.KeccakA,
            CodeHashChanges: [new CodeHashEntry(TestItem.KeccakB.ValueHash256, TestItem.KeccakC.ValueHash256, 42)],
            SlotCountChanges: [new SlotCountEntry(TestItem.KeccakD.ValueHash256, 1, 2)]);

        int reported = BlockDiffRecordDecoder.Instance.GetLength(record);
        byte[] encoded = Encode(record);

        Assert.That(encoded, Has.Length.EqualTo(reported));
    }

    [Test]
    public void TrailingDeltaFields_RoundTripExactly()
    {
        BlockDiffRecord original = new(
            BlockNumber: 314,
            StateRoot: TestItem.KeccakA,
            CodeHashChanges: [],
            SlotCountChanges: [],
            AccountTrieBytesDelta: 12_345,
            StorageTrieBytesDelta: -6_789,
            AccountsAddedDelta: 42);

        BlockDiffRecord roundTripped = Decode(Encode(original));

        Assert.That(roundTripped.AccountTrieBytesDelta, Is.EqualTo(12_345L));
        Assert.That(roundTripped.StorageTrieBytesDelta, Is.EqualTo(-6_789L));
        Assert.That(roundTripped.AccountsAddedDelta, Is.EqualTo(42L));
    }

    /// <summary>Pre-extension (legacy four-field) payloads must still parse, with missing trailing fields read as zero.</summary>
    [Test]
    public void OldEncoder_NewDecoder_TreatsMissingTrailingFieldsAsZero()
    {
        byte[] legacyBytes = EncodeLegacySchema(
            blockNumber: 7,
            stateRoot: TestItem.KeccakA,
            codeHashChanges: [new CodeHashEntry(CodeHashChange.NoCode, TestItem.KeccakB.ValueHash256, 24)],
            slotCountChanges: [new SlotCountEntry(TestItem.KeccakC.ValueHash256, 0, 9)]);

        BlockDiffRecord decoded = Decode(legacyBytes);

        Assert.That(decoded.BlockNumber, Is.EqualTo(7L));
        Assert.That(decoded.StateRoot, Is.EqualTo(TestItem.KeccakA));
        Assert.That(decoded.CodeHashChanges, Has.Count.EqualTo(1));
        Assert.That(decoded.SlotCountChanges, Has.Count.EqualTo(1));
        Assert.That(decoded.AccountTrieBytesDelta, Is.Zero);
        Assert.That(decoded.StorageTrieBytesDelta, Is.Zero);
        Assert.That(decoded.AccountsAddedDelta, Is.Zero);
    }

    /// <summary>New-encoder payloads must stay readable by a legacy four-field decoder reading only the prefix.</summary>
    [Test]
    public void NewEncoder_LegacyDecoder_IgnoresTrailingFields()
    {
        BlockDiffRecord original = new(
            BlockNumber: 91,
            StateRoot: TestItem.KeccakD,
            CodeHashChanges: [new CodeHashEntry(TestItem.KeccakA.ValueHash256, CodeHashChange.NoCode, 0)],
            SlotCountChanges: [new SlotCountEntry(TestItem.KeccakB.ValueHash256, 4, 5)],
            AccountTrieBytesDelta: 999,
            StorageTrieBytesDelta: -123,
            AccountsAddedDelta: 7);

        byte[] bytes = Encode(original);

        (long blockNumber, Hash256 stateRoot,
            List<CodeHashEntry> codeChanges, List<SlotCountEntry> slotChanges) =
            DecodeLegacyPrefix(bytes);

        Assert.That(blockNumber, Is.EqualTo(original.BlockNumber));
        Assert.That(stateRoot, Is.EqualTo(original.StateRoot));
        Assert.That(codeChanges, Is.EqualTo(original.CodeHashChanges));
        Assert.That(slotChanges, Is.EqualTo(original.SlotCountChanges));
    }

    /// <summary>Hand-rolled encoder emitting the pre-extension four-field layout, byte-identical to the v18 plugin.</summary>
    private static byte[] EncodeLegacySchema(
        long blockNumber,
        Hash256 stateRoot,
        IReadOnlyList<CodeHashEntry> codeHashChanges,
        IReadOnlyList<SlotCountEntry> slotCountChanges)
    {
        int codeSeqContent = 0;
        foreach (CodeHashEntry e in codeHashChanges)
        {
            int entryContent = LengthOfHashOrEmpty(e.OldHash)
                + LengthOfHashOrEmpty(e.NewHash)
                + Rlp.LengthOf(e.NewCodeSize);
            codeSeqContent += Rlp.LengthOfSequence(entryContent);
        }
        int slotSeqContent = 0;
        foreach (SlotCountEntry e in slotCountChanges)
        {
            int entryContent = 33 + Rlp.LengthOf(e.OldCount) + Rlp.LengthOf(e.NewCount);
            slotSeqContent += Rlp.LengthOfSequence(entryContent);
        }

        int outerContent = Rlp.LengthOf(blockNumber)
            + Rlp.LengthOf(stateRoot)
            + Rlp.LengthOfSequence(codeSeqContent)
            + Rlp.LengthOfSequence(slotSeqContent);

        int totalLength = Rlp.LengthOfSequence(outerContent);
        byte[] payload = new byte[totalLength];
        RlpWriter stream = new(payload);
        stream.StartSequence(outerContent);
        stream.Encode(blockNumber);
        stream.Encode(stateRoot);

        stream.StartSequence(codeSeqContent);
        foreach (CodeHashEntry e in codeHashChanges)
        {
            int entryContent = LengthOfHashOrEmpty(e.OldHash)
                + LengthOfHashOrEmpty(e.NewHash)
                + Rlp.LengthOf(e.NewCodeSize);
            stream.StartSequence(entryContent);
            EncodeHashOrEmpty(ref stream, e.OldHash);
            EncodeHashOrEmpty(ref stream, e.NewHash);
            stream.Encode(e.NewCodeSize);
        }

        stream.StartSequence(slotSeqContent);
        foreach (SlotCountEntry e in slotCountChanges)
        {
            int entryContent = 33 + Rlp.LengthOf(e.OldCount) + Rlp.LengthOf(e.NewCount);
            stream.StartSequence(entryContent);
            stream.Encode(e.AddressHash);
            stream.Encode(e.OldCount);
            stream.Encode(e.NewCount);
        }

        return payload;
    }

    /// <summary>Legacy-shape decoder reading only the first four positional fields, as the pre-extension code did.</summary>
    private static (long, Hash256, List<CodeHashEntry>, List<SlotCountEntry>) DecodeLegacyPrefix(byte[] bytes)
    {
        RlpReader ctx = new(bytes);
        ctx.ReadSequenceLength();

        long blockNumber = ctx.DecodeLong();
        Hash256 stateRoot = ctx.DecodeKeccak()
            ?? throw new RlpException("StateRoot must not be null");

        int codeSeqLen = ctx.ReadSequenceLength();
        int codeSeqEnd = ctx.Position + codeSeqLen;
        List<CodeHashEntry> codeChanges = [];
        while (ctx.Position < codeSeqEnd)
        {
            ctx.ReadSequenceLength();
            ValueHash256 oldHash = ctx.DecodeValueKeccak() ?? CodeHashChange.NoCode;
            ValueHash256 newHash = ctx.DecodeValueKeccak() ?? CodeHashChange.NoCode;
            uint newCodeSize = ctx.DecodeUInt();
            codeChanges.Add(new CodeHashEntry(oldHash, newHash, newCodeSize));
        }

        int slotSeqLen = ctx.ReadSequenceLength();
        int slotSeqEnd = ctx.Position + slotSeqLen;
        List<SlotCountEntry> slotChanges = [];
        while (ctx.Position < slotSeqEnd)
        {
            ctx.ReadSequenceLength();
            ValueHash256 addressHash = ctx.DecodeValueKeccak()
                ?? throw new RlpException("AddressHash must not be empty");
            ulong oldCount = ctx.DecodeULong();
            ulong newCount = ctx.DecodeULong();
            slotChanges.Add(new SlotCountEntry(addressHash, oldCount, newCount));
        }

        return (blockNumber, stateRoot, codeChanges, slotChanges);
    }

    private static void EncodeHashOrEmpty(ref RlpWriter stream, in ValueHash256 hash)
    {
        if (hash == CodeHashChange.NoCode)
        {
            stream.EncodeEmptyByteArray();
            return;
        }
        stream.Encode(in hash);
    }

    private static int LengthOfHashOrEmpty(in ValueHash256 hash)
        => hash == CodeHashChange.NoCode ? 1 : 33;
}
