// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Eez.Test.RollupX;

public partial class RollupXGenesisTests
{
    private static readonly NativeL1HeaderRules LegacyHeaderRules = new(null, null, null, null, null);

    [Test]
    public void NativeL1Header_ForkLayouts_MatchOnlyConfiguredFields([Values(15, 16, 17, 20, 21)] int fields,
        [Values(15, 16, 17, 20, 21)] int configured)
    {
        byte[] raw = L1HeaderBytes(99, 120, default, fields);
        NativeL1HeaderRules rules = LayoutRules(configured);
        if (fields != configured)
        {
            Assert.That(() => NativeL1Header.Decode(raw, in rules), Throws.TypeOf<EezSettlementException>());
            return;
        }
        NativeL1Header header = NativeL1Header.Decode(raw, in rules);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(header.Number, Is.EqualTo(99UL));
            Assert.That(header.Timestamp, Is.EqualTo(120UL));
            Assert.That(header.Hash, Is.EqualTo(ValueKeccak.Compute(raw)));
            Assert.That(header.PrevRandao, Is.EqualTo(OfflineHash(0x42)));
            Assert.That(header.Encoded.ToArray(), Is.EqualTo(raw));
        }
    }

    [Test]
    public void NativeL1Header_ZeroLondonBaseFee_RetainsItsPresentField()
    {
        byte[] raw = L1HeaderBytes(99, 120, default, 16, 15, Rlp.Encode(0UL));
        NativeL1HeaderRules rules = LayoutRules(16);
        Assert.That(NativeL1Header.Decode(raw, in rules).Encoded.ToArray(), Is.EqualTo(raw));
    }

    [TestCase(99UL, 100UL, 15)]
    [TestCase(100UL, 199UL, 16)]
    [TestCase(100UL, 200UL, 17)]
    [TestCase(100UL, 300UL, 20)]
    [TestCase(100UL, 400UL, 21)]
    public void NativeL1Header_ForkCoordinates_UseBlockAndTimestampBoundaries(ulong number, ulong timestamp, int fields)
    {
        NativeL1HeaderRules rules = new(100, 200, 300, 400, 500);
        Assert.That(NativeL1Header.Decode(L1HeaderBytes(number, timestamp, default, fields), in rules).Number, Is.EqualTo(number));
    }

    [Test]
    public void NativeL1Header_UnsupportedFork_RejectsAtActivation()
    {
        NativeL1HeaderRules rules = new(0, 0, 0, 0, 120);
        Assert.That(NativeL1Header.Decode(L1HeaderBytes(99, 119, default, 21), in rules).Timestamp, Is.EqualTo(119UL));
        Assert.That(() => NativeL1Header.Decode(L1HeaderBytes(99, 120, default, 21), in rules), Throws.TypeOf<EezSettlementException>());
    }

    private static IEnumerable<TestCaseData> InvalidL1FieldWidths() =>
    [
        new(0, 31), new(2, 19), new(6, 255), new(7, 33), new(8, 9), new(12, 33),
        new(13, 31), new(14, 7), new(15, 9), new(16, 31), new(17, 9), new(20, 31),
    ];

    [TestCaseSource(nameof(InvalidL1FieldWidths))]
    public void NativeL1Header_InvalidFieldWidth_Rejects(int field, int bytes)
    {
        byte[] value = new byte[bytes];
        value.AsSpan().Fill(1);
        byte[] raw = L1HeaderBytes(99, 120, default, 21, field, Rlp.Encode(value));
        NativeL1HeaderRules rules = LayoutRules(21);
        Assert.That(() => NativeL1Header.Decode(raw, in rules), Throws.TypeOf<EezSettlementException>());
    }

    private static readonly int[] L1IntegerFields = [7, 8, 9, 10, 11, 15, 17, 18];

    [Test]
    public void NativeL1Header_NoncanonicalInteger_Rejects([ValueSource(nameof(L1IntegerFields))] int field)
    {
        byte[] raw = L1HeaderBytes(99, 120, default, 21, field, Rlp.Encode(new byte[] { 0, 1 }));
        NativeL1HeaderRules rules = LayoutRules(21);
        Assert.That(() => NativeL1Header.Decode(raw, in rules), Throws.TypeOf<EezSettlementException>());
    }

    [Test]
    public void NativeL1Header_TruncatedOrTrailingRlp_Rejects()
    {
        byte[] raw = L1HeaderBytes(99, 120, default);
        for (int length = 0; length < raw.Length; length++)
        {
            byte[] prefix = raw[..length];
            Assert.That(() => NativeL1Header.Decode(prefix, LegacyHeaderRules), Throws.TypeOf<EezSettlementException>(), $"prefix {length}");
        }
        byte[] trailing = [.. raw, 0];
        Assert.That(() => NativeL1Header.Decode(trailing, LegacyHeaderRules), Throws.TypeOf<EezSettlementException>());
    }

    [Test]
    public void NativeL1Header_UnsupportedFieldCount_Rejects([Values(0, 14, 18, 19, 22, 23)] int fields) =>
        Assert.That(() => NativeL1Header.Decode(L1HeaderBytes(99, 120, default, fields), LayoutRules(Math.Min(fields, 20))), Throws.TypeOf<EezSettlementException>());

    [Test]
    public void NativeL1Header_MaximumDifficultyAndExtraData_Accepts([Values(7, 12)] int field)
    {
        byte[] value = new byte[32];
        value.AsSpan().Fill(0xff);
        byte[] raw = L1HeaderBytes(99, 120, default, 15, field, Rlp.Encode(value));
        Assert.That(NativeL1Header.Decode(raw, LegacyHeaderRules).Encoded.ToArray(), Is.EqualTo(raw));
    }

    [Test]
    public void NativeL1Header_FullWidthPosition_Accepts()
    {
        NativeL1Header header = NativeL1Header.Decode(L1HeaderBytes(ulong.MaxValue, ulong.MaxValue, default), LegacyHeaderRules);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(header.Number, Is.EqualTo(ulong.MaxValue));
            Assert.That(header.Timestamp, Is.EqualTo(ulong.MaxValue));
        }
    }

    [Test]
    public void NativeL1Header_CallerMutatesBuffer_RetainsItsOwnSnapshot()
    {
        byte[] raw = L1HeaderBytes(99, 120, default);
        byte[] original = (byte[])raw.Clone();
        NativeL1Header header = NativeL1Header.Decode(raw, LegacyHeaderRules);
        raw.AsSpan().Clear();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(header.Encoded.ToArray(), Is.EqualTo(original));
            Assert.That(header.Hash, Is.EqualTo(ValueKeccak.Compute(original)));
            Assert.That(header.Number, Is.EqualTo(99UL));
        }
    }

    [Test]
    public void NativeL1HeaderChain_BadSegment_DoesNotPublishItsValidPrefix([Values("gap", "parent", "time", "duplicate")] string field)
    {
        ReadOnlyMemory<byte>[] raw = L1ChainBytes();
        NativeL1HeaderChain chain = new();
        chain.Append(raw[..2], LegacyHeaderRules);
        byte[] bad = field switch
        {
            "gap" => L1HeaderBytes(103, 144, ValueKeccak.Compute(raw[2].Span)),
            "parent" => L1HeaderBytes(102, 144, OfflineHash(0x99)),
            "time" => L1HeaderBytes(102, 132, ValueKeccak.Compute(raw[2].Span)),
            _ => raw[2].ToArray(),
        };
        Assert.That(() => chain.Append([raw[2], bad], LegacyHeaderRules), Throws.TypeOf<EezSettlementException>());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.Count, Is.EqualTo(2));
            Assert.That(chain.Endpoint.Hash, Is.EqualTo(ValueKeccak.Compute(raw[1].Span)));
        }
        chain.Append(raw[2..], LegacyHeaderRules);
        Assert.That(chain.Count, Is.EqualTo(5));
    }

    [Test]
    public void NativeL1HeaderChain_MissingOrReorderedHeaders_Rejects([Values] bool reversed)
    {
        ReadOnlyMemory<byte>[] raw = L1ChainBytes();
        NativeL1HeaderChain chain = new();
        chain.Append(raw[..2], LegacyHeaderRules);
        Assert.That(() => chain.Append(reversed ? [raw[3], raw[2]] : raw[3..], LegacyHeaderRules), Throws.TypeOf<EezSettlementException>());
        Assert.That(chain.Count, Is.EqualTo(2));
    }

    [Test]
    public void NativeL1HeaderChain_CountLimitAndRewind_AreExact()
    {
        ReadOnlyMemory<byte>[] raw = L1ChainBytes();
        NativeL1HeaderChain chain = new(3);
        chain.Append(raw[..2], LegacyHeaderRules);
        NativeL1HeaderChain checkpoint = chain.Prefix(2);
        Assert.That(() => chain.Append(raw[2..4], LegacyHeaderRules), Throws.TypeOf<EezSettlementException>());
        chain.Append(raw[2..3], LegacyHeaderRules);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.Count, Is.EqualTo(3));
            Assert.That(checkpoint.Count, Is.EqualTo(2));
            Assert.That(checkpoint.Endpoint.Number, Is.EqualTo(100UL));
            Assert.That(chain.Prefix(0).Count, Is.Zero);
            Assert.That(() => checkpoint.Prefix(3), Throws.TypeOf<EezSettlementException>());
            Assert.That(() => checkpoint.Prefix(-1), Throws.TypeOf<EezSettlementException>());
        }
    }

    [TestCase(108UL, 99UL)]
    [TestCase(119UL, 99UL)]
    [TestCase(120UL, 100UL)]
    [TestCase(ulong.MaxValue, 103UL)]
    public void NativeL1HeaderChain_ProducerSelection_UsesLatestEligible(ulong cutoff, ulong expected)
    {
        NativeL1HeaderChain chain = new();
        chain.Append(L1ChainBytes(), LegacyHeaderRules);
        Assert.That(chain.Select(cutoff).Number, Is.EqualTo(expected));
    }

    [Test]
    public void NativeL1HeaderChain_AbsentEvidence_NeverFallsBackToAnInferredHeader()
    {
        NativeL1HeaderChain chain = new();
        Assert.That(() => chain.Endpoint, Throws.TypeOf<EezSettlementException>());
        Assert.That(() => chain.At(99), Throws.TypeOf<EezSettlementException>());
        chain.Append(L1ChainBytes(), LegacyHeaderRules);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => chain.At(98), Throws.TypeOf<EezSettlementException>());
            Assert.That(() => chain.At(104), Throws.TypeOf<EezSettlementException>());
            Assert.That(() => chain.Select(107), Throws.TypeOf<EezSettlementException>());
        }
    }

    [Test]
    public void NativeL1HeaderChain_NumberLookup_MatchesOriginalEvidence([Values(0, 1, 4)] int index)
    {
        ReadOnlyMemory<byte>[] raw = L1ChainBytes();
        NativeL1HeaderChain chain = new();
        chain.Append(raw, LegacyHeaderRules);
        ulong number = 99UL + (ulong)index;
        NativeL1Header header = chain.At(number);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(header.Number, Is.EqualTo(number));
            Assert.That(header.Hash, Is.EqualTo(ValueKeccak.Compute(raw[index].Span)));
        }
    }

    [Test]
    public void NativeL1HeaderChain_RecordedOrigin_RemainsBoundWithLaterEvidence([Values] bool actualParent)
    {
        NativeL1HeaderChain chain = new();
        chain.Append(L1ChainBytes(), LegacyHeaderRules);
        NativeEvidenceAnchor anchor = new(103, chain.Endpoint.Hash, actualParent ? 168UL : null);
        chain.VerifyOrigin(99, chain.At(99).Hash, in anchor);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(anchor.Selector, Is.EqualTo(actualParent ? ulong.MaxValue : 103UL));
            Assert.That(() => chain.VerifyOrigin(99, chain.At(100).Hash, in anchor), Throws.TypeOf<EezSettlementException>());
        }
    }

    [Test]
    public void NativeL1HeaderChain_ExactAnchor_RejectsWrongClaims([Values("number", "hash", "zero", "max", "time")] string field)
    {
        NativeL1HeaderChain chain = new();
        chain.Append(L1ChainBytes(), LegacyHeaderRules);
        NativeEvidenceAnchor anchor = field switch
        {
            "number" => new(102, chain.Endpoint.Hash, 168),
            "hash" => new(103, OfflineHash(0x77), 168),
            "zero" => new(103, default, 168),
            "max" => new(ulong.MaxValue, chain.Endpoint.Hash, 168),
            _ => new(103, chain.Endpoint.Hash, 156),
        };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => chain.VerifyAnchor(in anchor), Throws.TypeOf<EezSettlementException>());
            Assert.That(() => chain.VerifyOrigin(99, chain.At(99).Hash, in anchor), Throws.TypeOf<EezSettlementException>());
        }
    }

    [Test]
    public void NativeL1HeaderChain_ExtraSuffix_RejectsThePreviousAnchor()
    {
        ReadOnlyMemory<byte>[] raw = L1ChainBytes();
        NativeL1HeaderChain chain = new();
        chain.Append(raw[..2], LegacyHeaderRules);
        NativeEvidenceAnchor anchor = new(100, chain.Endpoint.Hash, null);
        ValueHash256 recorded = ValueKeccak.Compute(raw[0].Span);
        chain.VerifyOrigin(99, in recorded, in anchor);
        chain.Append(raw[2..3], LegacyHeaderRules);
        Assert.That(() => chain.VerifyOrigin(99, in recorded, in anchor), Throws.TypeOf<EezSettlementException>());
    }

    [Test]
    public void NativeL1HeaderChain_MaximumNumber_RejectsReservedAnchorsAndWraparound()
    {
        NativeL1HeaderChain chain = new();
        chain.Append([L1HeaderBytes(ulong.MaxValue, 108, default)], LegacyHeaderRules);
        NativeEvidenceAnchor historical = new(ulong.MaxValue, chain.Endpoint.Hash, null);
        NativeEvidenceAnchor actual = historical with { TargetTimestamp = 120 };
        byte[] next = L1HeaderBytes(0, 120, chain.Endpoint.Hash);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => chain.VerifyAnchor(in historical), Throws.TypeOf<EezSettlementException>());
            Assert.That(() => chain.VerifyAnchor(in actual), Throws.TypeOf<EezSettlementException>());
            Assert.That(() => chain.Append([next], LegacyHeaderRules), Throws.TypeOf<EezSettlementException>());
            Assert.That(chain.Count, Is.EqualTo(1));
        }
    }

    [Test]
    public void NativeL1HeaderChain_GenesisAnchor_AllowsOnlyActualParent()
    {
        NativeL1HeaderChain chain = new();
        chain.Append([L1HeaderBytes(0, 108, default)], LegacyHeaderRules);
        NativeEvidenceAnchor historical = new(0, chain.Endpoint.Hash, null);
        NativeEvidenceAnchor actual = new(0, chain.Endpoint.Hash, 120);
        Assert.That(() => chain.VerifyAnchor(in historical), Throws.TypeOf<EezSettlementException>());
        chain.VerifyAnchor(in actual);
    }

    private static NativeL1HeaderRules LayoutRules(int fields) =>
        new(fields >= 16 ? 0UL : null, fields >= 17 ? 0UL : null, fields >= 20 ? 0UL : null, fields >= 21 ? 0UL : null, null);

    private static ReadOnlyMemory<byte>[] L1ChainBytes() => L1ChainBytes([108, 120, 132, 144, 156]);

    private static ReadOnlyMemory<byte>[] L1ChainBytes(ulong[] times)
    {
        ReadOnlyMemory<byte>[] raw = new ReadOnlyMemory<byte>[times.Length];
        ValueHash256 parent = default;
        for (int index = 0; index < raw.Length; index++)
        {
            raw[index] = L1HeaderBytes((ulong)index + 99, times[index], parent, 15, 13, Rlp.Encode(OfflineHash((byte)(index + 1)).Bytes));
            parent = ValueKeccak.Compute(raw[index].Span);
        }
        return raw;
    }

    private static byte[] L1HeaderBytes(ulong number, ulong timestamp, ValueHash256 parent, int fields = 15, int replace = -1, Rlp? value = null)
    {
        Rlp[] parts =
        [
            Rlp.Encode(parent.Bytes), Rlp.Encode(Keccak.OfAnEmptySequenceRlp.Bytes), Rlp.Encode(new byte[20]),
            Rlp.Encode(Keccak.EmptyTreeHash.Bytes), Rlp.Encode(Keccak.EmptyTreeHash.Bytes), Rlp.Encode(Keccak.EmptyTreeHash.Bytes),
            Rlp.Encode(new byte[256]), Rlp.Encode(0UL), Rlp.Encode(number), Rlp.Encode(30_000_000UL), Rlp.Encode(0UL),
            Rlp.Encode(timestamp), Rlp.Encode(Array.Empty<byte>()), Rlp.Encode(OfflineHash(0x42).Bytes), Rlp.Encode(new byte[8]),
            Rlp.Encode(1UL), Rlp.Encode(Keccak.EmptyTreeHash.Bytes), Rlp.Encode(0UL), Rlp.Encode(0UL),
            Rlp.Encode(Keccak.Zero.Bytes), Rlp.Encode(Keccak.Zero.Bytes), Rlp.Encode(Keccak.Zero.Bytes), Rlp.Encode(0UL),
        ];
        if (replace >= 0) parts[replace] = value!;
        return Rlp.Encode(parts[..fields]).Bytes;
    }
}
