// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public partial class EezSystemTxDecoderTests
{
    private static IEnumerable<TestCaseData> NativeSourceVectors()
    {
        foreach (JsonElement vector in NativeSourceFixture().GetProperty("vectors").EnumerateArray())
        {
            string id = vector.GetProperty("id").GetString()!;
            if (id.StartsWith("history-", StringComparison.Ordinal) && !id.EndsWith("calldata", StringComparison.Ordinal))
                yield return new TestCaseData(vector).SetName($"NativeSource_Upstream_{id}");
        }
    }

    private static IEnumerable<TestCaseData> CurrentSourceVectors()
    {
        foreach (JsonElement vector in StatelessFixtures.ReadJson("rollupx/native-profile-v3", "vectors.json").GetProperty("vectors").EnumerateArray())
            if (vector.GetProperty("id").GetString()!.StartsWith("source-", StringComparison.Ordinal))
                yield return new TestCaseData(vector).SetName($"NativeSource_Upstream_{vector.GetProperty("id").GetString()}");
    }

    [TestCaseSource(nameof(CurrentSourceVectors))]
    public void NativeSource_CurrentVector_MatchesExactAbiAndHash(JsonElement vector)
    {
        JsonElement args = vector.GetProperty("arguments");
        NativeLiveSource source = new(NativeNumber(args[1]), new ValueHash256(args[2].GetString()!));
        ulong index = NativeNumber(args[3]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.EncodeEntry(index), Is.EqualTo(Bytes.FromHexString(vector.GetProperty("expectedBytes").GetString()!)));
            Assert.That(source.Entry(index), Is.EqualTo(new ValueHash256(vector.GetProperty("expectedKeccak256").GetString()!)));
        }
    }

    [TestCaseSource(nameof(NativeSourceVectors))]
    public void NativeSource_IndependentVector_MatchesHash(JsonElement vector)
    {
        string id = vector.GetProperty("id").GetString()!;
        JsonElement args = vector.GetProperty("arguments");
        NativeHistorySource source = new(1, 901, RepeatedHash(0x33));
        ValueHash256 actual;
        if (id == "history-domain") actual = source.Domain;
        else
        {
            byte[] data = Bytes.FromHexString(SourceVector(id == "history-advance-source" ? "history-advance-calldata" : "history-repeat-calldata")
                .GetProperty("expectedBytes").GetString()!);
            actual = source.Compute(new ValueHash256(args[1].GetString()!), NativeNumber(args[2]), NativeNumber(args[3]), data);
        }
        Assert.That(actual, Is.EqualTo(new ValueHash256(vector.GetProperty("expectedKeccak256").GetString()!)));
    }

    [Test]
    public void NativeSource_CoreCallHash_MatchesIndependentHash()
    {
        ValueHash256 actual = CrossChainCallHash.Compute(false, new Address("0x1111111111111111111111111111111111111111"), 0,
            new Address("0x2222222222222222222222222222222222222222"), 7, 5, 0, [0xaa, 0xbb]);
        Assert.That(actual, Is.EqualTo(new ValueHash256(SourceVector("core-call-hash").GetProperty("expectedKeccak256").GetString()!)));
    }

    [Test]
    public void NativeHistoryCall_IndependentCalldata_EncodesAndDecodes([Values] bool advance)
    {
        ValueHash256[] hashes = advance ? [RepeatedHash(0x99)] : [];
        byte[] expected = Bytes.FromHexString(SourceVector(advance ? "history-advance-calldata" : "history-repeat-calldata")
            .GetProperty("expectedBytes").GetString()!);
        byte[] actual = NativeHistoryCall.Encode(132, hashes);
        ReadOnlySpan<byte> decoded = NativeHistoryCall.Decode(expected, out ulong cutoff);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(cutoff, Is.EqualTo(132UL));
            Assert.That(decoded.ToArray(), Is.EqualTo(advance ? RepeatedHash(0x99).Bytes.ToArray() : Array.Empty<byte>()));
        }
    }

    [Test]
    public void NativeHistoryCall_TruncatedEncoding_RejectsEveryPrefix()
    {
        byte[] data = NativeHistoryCall.Encode(132, [RepeatedHash(0x99)]);
        for (int length = 0; length < data.Length; length++)
        {
            byte[] truncated = data[..length];
            Assert.That(() => NativeHistoryCall.Decode(truncated, out _).Length, Throws.TypeOf<EezAbiException>(), $"prefix length {length}");
        }
    }

    [Test]
    public void NativeHistoryCall_NoncanonicalAbi_Rejects([Values("selector", "cutoff", "offset", "count", "trailing", "excess")] string field)
    {
        byte[] data = NativeHistoryCall.Encode(132, []);
        switch (field)
        {
            case "selector": data[0] ^= 1; break;
            case "cutoff": data[4] = 1; break;
            case "offset": data[67] = 96; break;
            case "count": data[68] = 1; break;
            case "trailing": data = [.. data, 0]; break;
            case "excess":
                byte[] excess = new byte[100 + 65 * 32];
                data.CopyTo(excess, 0);
                excess[99] = 65;
                data = excess;
                break;
        }
        Assert.That(() => NativeHistoryCall.Decode(data, out _).Length, Throws.TypeOf<EezAbiException>());
    }

    [Test]
    public void NativeHistoryCall_MaximumImport_AcceptsAllWordsAndFullCutoff()
    {
        ValueHash256[] hashes = new ValueHash256[64];
        for (int index = 0; index < hashes.Length; index++) hashes[index] = RepeatedHash((byte)(index + 1));
        byte[] encoded = NativeHistoryCall.Encode(ulong.MaxValue, hashes);
        ReadOnlySpan<byte> decoded = NativeHistoryCall.Decode(encoded, out ulong cutoff);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cutoff, Is.EqualTo(ulong.MaxValue));
            for (int index = 0; index < hashes.Length; index++)
                Assert.That(new ValueHash256(decoded.Slice(index * 32, 32)), Is.EqualTo(hashes[index]));
        }
        Assert.That(() => NativeHistoryCall.Encode(132, new ValueHash256[65]), Throws.TypeOf<EezAbiException>());
    }

    [Test]
    public void NativeSource_GroupIdentity_BindsOriginalOrdinalAndContext([Values("chain", "parent", "index")] string field)
    {
        NativeLiveSource baseline = new(901, RepeatedHash(0x88));
        NativeLiveSource changed = new(field == "chain" ? ulong.MaxValue : 901UL,
            RepeatedHash(field == "parent" ? (byte)0x89 : (byte)0x88));
        Assert.That(changed.Entry(field == "index" ? ulong.MaxValue : 3UL), Is.Not.EqualTo(baseline.Entry(3)));
    }

    [Test]
    public void NativeSource_ConsumedSuffix_RetainsCompleteOriginalGroupOrdinal()
    {
        JsonElement vectors = StatelessFixtures.ReadJson("rollupx/native-profile-v3", "vectors.json").GetProperty("vectors");
        ValueHash256 original = default, consumed = default;
        foreach (JsonElement vector in vectors.EnumerateArray())
        {
            if (vector.GetProperty("id").GetString() == "source-base") original = new ValueHash256(vector.GetProperty("expectedKeccak256").GetString()!);
            if (vector.GetProperty("id").GetString() == "source-after-prefix-consumed") consumed = new ValueHash256(vector.GetProperty("expectedKeccak256").GetString()!);
        }
        NativeLiveSource source = new(901, RepeatedHash(0x88));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(original, Is.Not.EqualTo(default(ValueHash256)));
            Assert.That(source.Entry(3), Is.EqualTo(original));
            Assert.That(source.Entry(3), Is.EqualTo(consumed));
            Assert.That(source.Entry(0), Is.Not.EqualTo(consumed));
        }
    }

    [Test]
    public void NativeSource_NonzeroHistoryContext_BindsEveryDomainField([Values("l1", "l2", "genesis")] string field)
    {
        NativeHistorySource baseline = new(1, 901, RepeatedHash(0x33));
        NativeHistorySource changed = new(field == "l1" ? ulong.MaxValue : 1UL, field == "l2" ? ulong.MaxValue : 901UL,
            RepeatedHash(field == "genesis" ? (byte)0x34 : (byte)0x33));
        Assert.That(changed.Domain, Is.Not.EqualTo(baseline.Domain));
    }

    [Test]
    public void NativeSource_MissingLiveContext_Rejects([Values("chain", "parent")] string field) =>
        Assert.That(() => new NativeLiveSource(field == "chain" ? 0UL : 901UL,
            field == "parent" ? default : RepeatedHash(0x88)), Throws.TypeOf<ArgumentException>());

    [Test]
    public void NativeSource_MissingHistoryContext_Rejects([Values("l1", "l2", "genesis")] string field) =>
        Assert.That(() => new NativeHistorySource(field == "l1" ? 0UL : 1UL, field == "l2" ? 0UL : 901UL,
            field == "genesis" ? default : RepeatedHash(0x33)), Throws.TypeOf<ArgumentException>());

    [Test]
    public void NativeSource_MissingHistoryPosition_Rejects([Values("parent", "number", "timestamp")] string field)
    {
        NativeHistorySource source = new(1, 901, RepeatedHash(0x33));
        byte[] data = NativeHistoryCall.Encode(132, []);
        Assert.That(() => source.Compute(field == "parent" ? default : RepeatedHash(0x77), field == "number" ? 0UL : 9UL,
            field == "timestamp" ? 0UL : 138UL, data), Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public void NativeSource_HistoryTrailingData_RejectsBeforeHashing()
    {
        NativeHistorySource source = new(1, 901, RepeatedHash(0x33));
        byte[] trailing = [.. NativeHistoryCall.Encode(132, []), 0];
        Assert.That(() => source.Compute(RepeatedHash(0x77), 9, 138, trailing), Throws.TypeOf<EezAbiException>());
    }

    private static ValueHash256 RepeatedHash(byte value)
    {
        Span<byte> bytes = stackalloc byte[32];
        bytes.Fill(value);
        return new ValueHash256(bytes);
    }

    private static ulong NativeNumber(JsonElement value) => ulong.Parse(value.GetString()!, CultureInfo.InvariantCulture);
    private static JsonElement NativeSourceFixture() => StatelessFixtures.ReadJson("rollupx/native-profile-v1", "vectors.json");
    private static JsonElement SourceVector(string id)
    {
        foreach (JsonElement vector in NativeSourceFixture().GetProperty("vectors").EnumerateArray())
            if (vector.GetProperty("id").GetString() == id) return vector;
        throw new InvalidOperationException($"Missing native source vector {id}.");
    }
}
