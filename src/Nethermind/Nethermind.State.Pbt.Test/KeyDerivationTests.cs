// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class KeyDerivationTests
{
    private static readonly Address Address = TestItem.AddressA;

    [Test]
    public void AccountHeaderStemMatchesEipBitLayout()
    {
        Stem stem = PbtKeyDerivation.AccountHeaderStem(Address);

        byte[] expected = SpliceBits([0, 0, 0, 0], (Blake3(Address32(Address)), 244));
        Assert.That(stem.Bytes.SequenceEqual(expected));
        Assert.That(stem.Zone, Is.EqualTo(0));
        Assert.That(stem.IsStorageZone, Is.False);
    }

    [Test]
    public void StorageKeysMatchEipTestVectors()
    {
        // slot 5 lives in the account header at sub-index HEADER_STORAGE_OFFSET + 5 = 0x45
        Assert.That(PbtKeyDerivation.IsHeaderSlot(5), Is.True);
        Assert.That(PbtKeyDerivation.HeaderSlotSubIndex(5), Is.EqualTo(0x45));

        // slot 1000: tree_index = 3, sub_index = 232, stem = 1 || H(A)[:60] || H(A || 3)[:187]
        Assert.That(PbtKeyDerivation.IsHeaderSlot(1000), Is.False);
        Stem stem = PbtKeyDerivation.StorageStem(Address, 1000, out byte subIndex);
        Assert.That(subIndex, Is.EqualTo(0xE8));

        byte[] treeIndex = new byte[32];
        treeIndex[31] = 3;
        byte[] expected = SpliceBits([1], (Blake3(Address32(Address)), 60), (Blake3([.. Address32(Address), .. treeIndex]), 187));
        Assert.That(stem.Bytes.SequenceEqual(expected));
        Assert.That(stem.IsStorageZone, Is.True);
    }

    [Test]
    public void CodeChunkKeysMatchEipTestVectors()
    {
        // chunk 5 lives in the account header at sub-index CODE_OFFSET + 5 = 0x85
        Assert.That(PbtKeyDerivation.HeaderCodeChunkSubIndex(5), Is.EqualTo(0x85));

        // chunk 300: overflow = 172, tree_index = 0, sub_index = 0xAC, stem = 0x1 || H(C || 0)[:244]
        ValueHash256 codeHash = new("0xdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef");
        Stem stem = PbtKeyDerivation.CodeOverflowStem(codeHash, 300, out byte subIndex);
        Assert.That(subIndex, Is.EqualTo(0xAC));

        byte[] expected = SpliceBits([0, 0, 0, 1], (Blake3([.. codeHash.Bytes, .. new byte[32]]), 244));
        Assert.That(stem.Bytes.SequenceEqual(expected));
        Assert.That(stem.Zone, Is.EqualTo(1));
    }

    [Test]
    public void ChunkifyCodeRecordsLeadingPushData()
    {
        Assert.That(PbtKeyDerivation.ChunkifyCode([]), Is.Empty);

        // the EIP example: ... PUSH4 99 98 | 97 96 PUSH1 128 MSTORE — the second chunk starts
        // with 2 leading PUSHDATA bytes
        byte[] code = [.. new byte[28], 0x63, 99, 98, 97, 96, 0x60, 128, 0x52];
        byte[] chunks = PbtKeyDerivation.ChunkifyCode(code);
        Assert.That(chunks, Has.Length.EqualTo(2 * PbtKeyDerivation.CodeChunkSize));
        Assert.That(Chunk(chunks, 0)[0], Is.EqualTo(0));
        Assert.That(Chunk(chunks, 0)[1..].SequenceEqual(code.AsSpan(0, 31)));
        Assert.That(Chunk(chunks, 1)[0], Is.EqualTo(2));
        Assert.That(Chunk(chunks, 1).Slice(1, 5).SequenceEqual((byte[])[97, 96, 0x60, 128, 0x52]));
        Assert.That(Chunk(chunks, 1)[6..].IsZero(), "padding must be zero");

        // PUSH32 data spanning a whole chunk is capped at 31, with the tail counted in the next chunk
        byte[] pushData = new byte[32];
        Array.Fill(pushData, (byte)0xAA);
        byte[] push32Code = [.. new byte[30], 0x7F, .. pushData];
        chunks = PbtKeyDerivation.ChunkifyCode(push32Code);
        Assert.That(chunks, Has.Length.EqualTo(3 * PbtKeyDerivation.CodeChunkSize));
        Assert.That(Chunk(chunks, 1)[0], Is.EqualTo(31), "a fully-PUSHDATA chunk is capped at 31");
        Assert.That(Chunk(chunks, 2)[0], Is.EqualTo(1), "one PUSHDATA byte remains in the last chunk");
    }

    [Test]
    public void PackBasicDataLayout()
    {
        byte[] packed = new byte[32];
        UInt256 balance = new(Bytes.FromHexString("0x99887766554433221100ffeeddccbbaa"), isBigEndian: true);
        PbtKeyDerivation.PackBasicData(packed, 0xAABBCCDD, new UInt256(0x0102030405060708), balance);

        Assert.That(packed.ToHexString(), Is.EqualTo("00000000aabbccdd010203040506070899887766554433221100ffeeddccbbaa"));
    }

    private static ReadOnlySpan<byte> Chunk(byte[] chunks, int chunkId) =>
        chunks.AsSpan(chunkId * PbtKeyDerivation.CodeChunkSize, PbtKeyDerivation.CodeChunkSize);

    private static byte[] Address32(Address address) => [.. new byte[12], .. address.Bytes];

    private static byte[] Blake3(byte[] input)
    {
        byte[] output = new byte[32];
        global::Blake3.Hasher.Hash(input, output);
        return output;
    }

    /// <summary>Reference bit-splicing: concatenates bit segments MSB-first into a 31-byte stem, per the EIP's list-of-bits construction.</summary>
    private static byte[] SpliceBits(int[] leadingBits, params (byte[] Bytes, int BitCount)[] segments)
    {
        List<int> bits = [.. leadingBits];
        foreach ((byte[] bytes, int bitCount) in segments)
        {
            for (int i = 0; i < bitCount; i++)
            {
                bits.Add((bytes[i >> 3] >> (7 - (i & 7))) & 1);
            }
        }

        Assert.That(bits, Has.Count.EqualTo(Stem.LengthInBits));
        byte[] stem = new byte[Stem.Length];
        for (int i = 0; i < bits.Count; i++)
        {
            if (bits[i] != 0) stem[i >> 3] |= (byte)(1 << (7 - (i & 7)));
        }

        return stem;
    }
}
