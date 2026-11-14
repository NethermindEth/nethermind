// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Encoding;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Serialization.FluentRlp;
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
            Assert.That(() => NativeHistoryEnvelope.CreateBlocks(901, blocks), Throws.InstanceOf<RlpReaderException>());
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
            Assert.That(() => { _ = decoded.Hashes.Length; }, Throws.InstanceOf<RlpReaderException>(), "legacy consumers cannot reinterpret explicit pairs");
        }
    }

    [Test]
    public void DecodeBlocks_TruncatedOrWrongProfile_Rejects()
    {
        byte[] encoded = NativeHistoryEnvelope.CreateBlocks(ulong.MaxValue, [new(ulong.MaxValue, RepeatedHash(1))]).Encode();
        for (int length = 0; length < encoded.Length; length++)
        {
            byte[] truncated = encoded[..length];
            Assert.That(() => NativeHistoryEnvelope.DecodeBlocks(truncated), Throws.InstanceOf<RlpReaderException>(), $"length {length}");
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => NativeHistoryEnvelope.Decode(encoded), Throws.InstanceOf<RlpReaderException>(), "old profile selection stays explicit");
            byte[] old = NativeHistoryEnvelope.Create(901, [RepeatedHash(1)]).Encode();
            Assert.That(() => NativeHistoryEnvelope.DecodeBlocks(old), Throws.InstanceOf<RlpReaderException>(), "new profile cannot accept a legacy hash list");
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
        Assert.That(() => NativeHistoryEnvelope.CreateBlocks(901, blocks), Throws.InstanceOf<RlpReaderException>());
    }

    [Test]
    public void DecodeBlocks_NoncanonicalOrInvalidRecord_Rejects(
        [Values("empty", "zero", "duplicate", "descending", "zero-hash", "short-hash", "long-hash", "number-wide", "number-zero-prefix", "number-long", "pair-string", "missing-hash", "extra-field", "count", "list-string", "trailing", "type", "version", "version-long", "chain-wide", "chain-zero-prefix", "pair-long", "list-long", "outer-long")] string field)
    {
        byte[] hash = Rlp.Encode(RepeatedHash(1).Bytes);
        if (field == "zero-hash") hash = Rlp.Encode(new byte[Hash256.Size]);
        if (field == "short-hash") hash = Rlp.Encode(new byte[31]);
        if (field == "long-hash") hash = Rlp.Encode(new byte[33]);
        byte[] number = field switch
        {
            "zero" => Rlp.Encode(0UL),
            "number-wide" => Rlp.Encode(new byte[] { 1, 1, 1, 1, 1, 1, 1, 1, 1 }),
            "number-zero-prefix" => [0x82, 0, 1],
            "number-long" => [0x81, 1],
            _ => Rlp.Encode(1UL),
        };
        byte[] pair = field switch
        {
            "pair-string" => hash,
            "missing-hash" => RlpTestHelper.EncodeSequence(number),
            "extra-field" => RlpTestHelper.EncodeSequence(number, hash, Rlp.Encode(0UL)),
            "pair-long" => [0xf8, 34, .. RlpTestHelper.EncodeSequence(number, hash).AsSpan(1)],
            _ => RlpTestHelper.EncodeSequence(number, hash),
        };
        byte[][] blocks = field switch
        {
            "empty" => [],
            "duplicate" => [pair, pair],
            "descending" => [RlpTestHelper.EncodeSequence(Rlp.Encode(2UL), hash), pair],
            _ => [pair],
        };
        if (field == "count")
        {
            blocks = new byte[65][];
            for (int index = 0; index < blocks.Length; index++) blocks[index] = RlpTestHelper.EncodeSequence(Rlp.Encode((ulong)index + 1), hash);
        }
        byte[] records = field == "list-string" ? Rlp.Encode(new byte[35]) : RlpTestHelper.EncodeSequence(blocks);
        if (field == "list-long") records = [0xf8, (byte)pair.Length, .. pair];
        byte[] version = field == "version-long" ? [0x81, 1] : Rlp.Encode(field == "version" ? 2UL : 1UL);
        byte[] chain = field switch
        {
            "chain-wide" => Rlp.Encode(new byte[] { 1, 1, 1, 1, 1, 1, 1, 1, 1 }),
            "chain-zero-prefix" => [0x82, 0, 1],
            _ => Rlp.Encode(901UL),
        };
        byte[] payload = RlpTestHelper.EncodeSequence(version, chain, records);
        byte[] encoded = [field == "type" ? (byte)0x45 : (byte)0x46, .. payload];
        if (field == "trailing") encoded = [.. encoded, 0];
        if (field == "outer-long") encoded = [0x46, 0xf8, (byte)(payload.Length - 1), .. payload.AsSpan(1)];
        Assert.That(() => NativeHistoryEnvelope.DecodeBlocks(encoded), Throws.InstanceOf<RlpReaderException>(), field);
    }

    private static NativeHistoryBlock[] HistoryBlocks(int count)
    {
        NativeHistoryBlock[] blocks = new NativeHistoryBlock[count];
        for (int index = 0; index < count; index++) blocks[index] = new((ulong)index + 1, RepeatedHash(1));
        return blocks;
    }
}
