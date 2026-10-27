// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public partial class EezSystemTxDecoderTests
{
    private static IEnumerable<TestCaseData> HistoryVectors()
    {
        foreach (JsonElement vector in StatelessFixtures.ReadJson("rollupx/native-profile-v3", "vectors.json").GetProperty("vectors").EnumerateArray())
            if (vector.TryGetProperty("prefix", out JsonElement prefix) && prefix.GetString() == "0x46")
                yield return new TestCaseData(vector).SetName($"HistoryEnvelope_Upstream_{vector.GetProperty("id").GetString()}");
    }

    [TestCaseSource(nameof(HistoryVectors))]
    public void Decode_UpstreamVector_RetainsExactFieldsBytesAndRepeatedHash(JsonElement vector)
    {
        byte[] bytes = Bytes.FromHexString(vector.GetProperty("expectedBytes").GetString()!);
        NativeHistoryEnvelope decoded = NativeHistoryEnvelope.Decode(bytes);
        JsonElement hashes = vector.GetProperty("fields")[2];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.ChainId, Is.EqualTo(901));
            Assert.That(decoded.Hashes.Length, Is.EqualTo(hashes.GetArrayLength()));
            for (int index = 0; index < hashes.GetArrayLength(); index++)
                Assert.That(decoded.Hashes[index], Is.EqualTo(new ValueHash256(hashes[index].GetString()!)));
            Assert.That(decoded.Encode(), Is.EqualTo(bytes));
            Assert.That(ValueKeccak.Compute(decoded.Encode()), Is.EqualTo(new ValueHash256(vector.GetProperty("expectedKeccak256").GetString()!)));
        }
    }

    [Test]
    public void Decode_EveryTruncatedPrefix_Rejects()
    {
        byte[] encoded = NativeHistoryEnvelope.Create(ulong.MaxValue, [RepeatedHash(1)]).Encode();
        for (int length = 0; length < encoded.Length; length++)
        {
            byte[] truncated = encoded[..length];
            Assert.That(() => NativeHistoryEnvelope.Decode(truncated), Throws.TypeOf<RlpException>(), $"length {length}");
        }
    }

    [Test]
    public void Create_HashCountBoundaries_EnforceOneTo64([Values(0, 1, 64, 65)] int count)
    {
        ValueHash256[] hashes = new ValueHash256[count];
        for (int index = 0; index < count; index++) hashes[index] = RepeatedHash((byte)(index + 1));
        if (count is 0 or 65) Assert.That(() => NativeHistoryEnvelope.Create(901, hashes), Throws.TypeOf<RlpException>());
        else Assert.That(NativeHistoryEnvelope.Decode(NativeHistoryEnvelope.Create(901, hashes).Encode()).Hashes.Length, Is.EqualTo(count));
    }

    [Test]
    public void Create_ChainIdUint64Boundaries_RoundTrip([Values(0UL, 1UL, ulong.MaxValue)] ulong chainId, [Values(1, 64)] int count)
    {
        ValueHash256[] hashes = new ValueHash256[count];
        Array.Fill(hashes, RepeatedHash(1));
        NativeHistoryEnvelope envelope = NativeHistoryEnvelope.Create(chainId, hashes);
        Assert.That(NativeHistoryEnvelope.Decode(envelope.Encode()).ChainId, Is.EqualTo(chainId));
    }

    [Test]
    public void Create_ZeroHashAtAnyPosition_Rejects([Values(0, 31, 63)] int position)
    {
        ValueHash256[] hashes = new ValueHash256[64];
        Array.Fill(hashes, RepeatedHash(1));
        hashes[position] = default;
        Assert.That(() => NativeHistoryEnvelope.Create(901, hashes), Throws.TypeOf<RlpException>());
    }

    [Test]
    public void CreateAndDecode_CallerMutation_DoesNotChangeOwnedEnvelope()
    {
        ValueHash256[] hashes = [RepeatedHash(1)];
        NativeHistoryEnvelope created = NativeHistoryEnvelope.Create(901, hashes);
        hashes[0] = RepeatedHash(2);
        byte[] encoded = created.Encode();
        NativeHistoryEnvelope decoded = NativeHistoryEnvelope.Decode(encoded);
        Array.Clear(encoded);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(created.Hashes[0], Is.EqualTo(RepeatedHash(1)));
            Assert.That(decoded.Hashes[0], Is.EqualTo(RepeatedHash(1)));
        }
    }

    private static readonly string[] InvalidHistoryEnvelopeFields =
        ["type", "version", "empty", "zero", "short-hash", "long-hash", "hash-list", "trailing", "extra-field", "outer-long", "list-long", "version-long", "chain-zero", "chain-wide", "count"];

    [Test]
    public void Decode_InvalidOrNoncanonicalEnvelope_Rejects([ValueSource(nameof(InvalidHistoryEnvelopeFields))] string field)
    {
        Rlp encodedHash = Rlp.Encode(RepeatedHash(1).Bytes.ToArray());
        Rlp[] hashes = [encodedHash];
        if (field == "empty") hashes = [];
        if (field == "zero") hashes = [Rlp.Encode(new byte[Hash256.Size])];
        if (field == "short-hash") hashes = [Rlp.Encode(new byte[31])];
        if (field == "long-hash") hashes = [Rlp.Encode(new byte[33])];
        if (field == "count") { hashes = new Rlp[65]; Array.Fill(hashes, encodedHash); }
        Rlp version = field == "version-long" ? new Rlp([0x81, 0x01]) : Rlp.Encode(field == "version" ? 2UL : 1UL);
        Rlp chain = field switch
        {
            "chain-zero" => new Rlp([0x82, 0x00, 0x01]),
            "chain-wide" => Rlp.Encode(new byte[] { 1, 1, 1, 1, 1, 1, 1, 1, 1 }),
            _ => Rlp.Encode(901UL)
        };
        Rlp list = field == "hash-list" ? Rlp.Encode(new byte[33]) : Rlp.Encode(hashes);
        if (field == "list-long") list = new Rlp([0xf8, 0x21, .. encodedHash.Bytes]);
        Rlp payload = field == "extra-field" ? Rlp.Encode(version, chain, list, Rlp.Encode(0UL)) : Rlp.Encode(version, chain, list);
        byte[] encoded = [field == "type" ? (byte)0x45 : (byte)0x46, .. payload.Bytes];
        if (field == "trailing") encoded = [.. encoded, 0];
        if (field == "outer-long") encoded = [0x46, 0xf8, (byte)(payload.Bytes.Length - 1), .. payload.Bytes.AsSpan(1)];
        Assert.That(() => NativeHistoryEnvelope.Decode(encoded), Throws.TypeOf<RlpException>());
    }

}
