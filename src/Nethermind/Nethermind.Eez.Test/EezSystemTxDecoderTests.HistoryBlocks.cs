// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public partial class EezSystemTxDecoderTests
{
    [Test]
    public void CreateBlocks_CountBoundaries_EnforcesOneTo64([Values(0, 1, 64, 65)] int count)
    {
        NativeHistoryBlock[] blocks = HistoryBlocks(count);
        if (count is 0 or 65)
            Assert.That(() => NativeHistoryEnvelope.CreateBlocks(901, blocks), Throws.TypeOf<RlpException>());
        else
            Assert.That(NativeHistoryEnvelope.DecodeBlocks(NativeHistoryEnvelope.CreateBlocks(901, blocks).Encode()).Blocks.Length, Is.EqualTo(count));
    }

    [Test]
    public void CreateBlocks_SparseNumbersAndRepeatedHashes_OwnsExactPairs([Values(0UL, 1UL, ulong.MaxValue)] ulong chainId)
    {
        NativeHistoryBlock[] expected = [new(7, RepeatedHash(1)), new(256, RepeatedHash(1)), new(ulong.MaxValue, RepeatedHash(2))];
        NativeHistoryBlock[] supplied = expected.AsSpan().ToArray();
        NativeHistoryEnvelope created = NativeHistoryEnvelope.CreateBlocks(chainId, supplied);
        Array.Clear(supplied);
        byte[] encoded = created.Encode();
        NativeHistoryEnvelope decoded = NativeHistoryEnvelope.DecodeBlocks(encoded);
        Array.Clear(encoded);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(created.Blocks.ToArray(), Is.EqualTo(expected), "caller mutation cannot change owned records");
            Assert.That(decoded.Blocks.ToArray(), Is.EqualTo(expected), "decoding retains gaps and repeated hashes without inventing records");
            Assert.That(decoded.ChainId, Is.EqualTo(chainId));
            Assert.That(() => { _ = decoded.Hashes.Length; }, Throws.TypeOf<RlpException>(), "legacy consumers cannot reinterpret explicit pairs");
        }
    }

    [Test]
    public void DecodeBlocks_TruncatedOrWrongProfile_Rejects()
    {
        byte[] encoded = NativeHistoryEnvelope.CreateBlocks(ulong.MaxValue, [new(ulong.MaxValue, RepeatedHash(1))]).Encode();
        for (int length = 0; length < encoded.Length; length++)
        {
            byte[] truncated = encoded[..length];
            Assert.That(() => NativeHistoryEnvelope.DecodeBlocks(truncated), Throws.TypeOf<RlpException>(), $"length {length}");
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => NativeHistoryEnvelope.Decode(encoded), Throws.TypeOf<RlpException>(), "old profile selection stays explicit");
            byte[] old = NativeHistoryEnvelope.Create(901, [RepeatedHash(1)]).Encode();
            Assert.That(() => NativeHistoryEnvelope.DecodeBlocks(old), Throws.TypeOf<RlpException>(), "new profile cannot accept a legacy hash list");
        }
    }

    [Test]
    public void CreateBlocks_InvalidNumberOrHash_Rejects([Values("zero", "duplicate", "descending", "hash")] string field)
    {
        NativeHistoryBlock[] blocks = [new(1, RepeatedHash(1)), new(2, RepeatedHash(2))];
        blocks[1] = field switch
        {
            "zero" => new(0, RepeatedHash(2)),
            "duplicate" => new(1, RepeatedHash(2)),
            "descending" => new(1, RepeatedHash(2)),
            _ => new(2, default),
        };
        if (field == "descending") blocks[0] = new(2, RepeatedHash(1));
        Assert.That(() => NativeHistoryEnvelope.CreateBlocks(901, blocks), Throws.TypeOf<RlpException>());
    }

    [Test]
    public void DecodeBlocks_NoncanonicalOrInvalidRecord_Rejects(
        [Values("empty", "zero", "duplicate", "descending", "zero-hash", "short-hash", "long-hash", "number-wide", "number-zero-prefix", "number-long", "pair-string", "missing-hash", "extra-field", "count", "list-string", "trailing", "type", "version", "version-long", "chain-wide", "chain-zero-prefix", "pair-long", "list-long", "outer-long")] string field)
    {
        Rlp hash = Rlp.Encode(RepeatedHash(1).Bytes);
        if (field == "zero-hash") hash = Rlp.Encode(new byte[Hash256.Size]);
        if (field == "short-hash") hash = Rlp.Encode(new byte[31]);
        if (field == "long-hash") hash = Rlp.Encode(new byte[33]);
        Rlp number = field switch
        {
            "zero" => Rlp.Encode(0UL),
            "number-wide" => Rlp.Encode(new byte[] { 1, 1, 1, 1, 1, 1, 1, 1, 1 }),
            "number-zero-prefix" => new Rlp([0x82, 0, 1]),
            "number-long" => new Rlp([0x81, 1]),
            _ => Rlp.Encode(1UL),
        };
        Rlp pair = field switch
        {
            "pair-string" => hash,
            "missing-hash" => Rlp.Encode(number),
            "extra-field" => Rlp.Encode(number, hash, Rlp.Encode(0UL)),
            "pair-long" => new Rlp([0xf8, 34, .. Rlp.Encode(number, hash).Bytes.AsSpan(1)]),
            _ => Rlp.Encode(number, hash),
        };
        Rlp[] blocks = field switch
        {
            "empty" => [],
            "duplicate" => [pair, pair],
            "descending" => [Rlp.Encode(Rlp.Encode(2UL), hash), pair],
            _ => [pair],
        };
        if (field == "count")
        {
            blocks = new Rlp[65];
            for (int index = 0; index < blocks.Length; index++) blocks[index] = Rlp.Encode(Rlp.Encode((ulong)index + 1), hash);
        }
        Rlp records = field == "list-string" ? Rlp.Encode(new byte[35]) : Rlp.Encode(blocks);
        if (field == "list-long") records = new Rlp([0xf8, (byte)pair.Bytes.Length, .. pair.Bytes]);
        Rlp version = field == "version-long" ? new Rlp([0x81, 1]) : Rlp.Encode(field == "version" ? 2UL : 1UL);
        Rlp chain = field switch
        {
            "chain-wide" => Rlp.Encode(new byte[] { 1, 1, 1, 1, 1, 1, 1, 1, 1 }),
            "chain-zero-prefix" => new Rlp([0x82, 0, 1]),
            _ => Rlp.Encode(901UL),
        };
        Rlp payload = Rlp.Encode(version, chain, records);
        byte[] encoded = [field == "type" ? (byte)0x45 : (byte)0x46, .. payload.Bytes];
        if (field == "trailing") encoded = [.. encoded, 0];
        if (field == "outer-long") encoded = [0x46, 0xf8, (byte)(payload.Bytes.Length - 1), .. payload.Bytes.AsSpan(1)];
        Assert.That(() => NativeHistoryEnvelope.DecodeBlocks(encoded), Throws.TypeOf<RlpException>(), field);
    }

    private static NativeHistoryBlock[] HistoryBlocks(int count)
    {
        NativeHistoryBlock[] blocks = new NativeHistoryBlock[count];
        for (int index = 0; index < count; index++) blocks[index] = new((ulong)index + 1, RepeatedHash(1));
        return blocks;
    }
}
