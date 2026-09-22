// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
    [Test]
    public void CodeChunkKeysMatchEipTestVectors([Values(0, 5, 127, 128, 255, 256, 300)] int chunkId)
    {
        ValueHash256 codeHash = TestItem.KeccakA.ValueHash256;
        PbtPath expected = PbtReferenceModel.CodeKey(codeHash, chunkId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Eip8297KeyDerivation.OverflowCodeKey(codeHash.Bytes, chunkId), Is.EqualTo(expected));
            Assert.That(PbtStateKey.Code(TestItem.AddressA, codeHash, chunkId), Is.EqualTo(expected));
            Assert.That(PbtStateKey.Code(PbtKeyDerivation.AddressKeyHash(TestItem.AddressB), codeHash, chunkId), Is.EqualTo(expected));
        }
    }

    [TestCase(-1, 32)]
    [TestCase(0, 31)]
    [TestCase(0, 33)]
    public void CodeChunkKeysRejectInvalidInputs(int chunkId, int hashLength)
    {
        byte[] codeHash = new byte[hashLength];
        Assert.That(() => Eip8297KeyDerivation.OverflowCodeKey(codeHash, chunkId), Throws.InstanceOf<ArgumentException>());
    }

    [Test]
    public void ChunkifyCodeRecordsLeadingPushData()
    {
        Assert.That(PbtKeyDerivation.ChunkifyCode([]), Is.Empty);

        // EIP-8297 example: the second chunk starts with two PUSHDATA bytes.
        byte[] code = [.. new byte[28], 0x63, 99, 98, 97, 96, 0x60, 128, 0x52];
        byte[] chunks = PbtKeyDerivation.ChunkifyCode(code);
        Assert.That(chunks, Has.Length.EqualTo(2 * PbtKeyDerivation.CodeChunkSize));
        Assert.That(Chunk(chunks, 0)[0], Is.EqualTo(0));
        Assert.That(Chunk(chunks, 0)[1..].SequenceEqual(code.AsSpan(0, 31)));
        Assert.That(Chunk(chunks, 1)[0], Is.EqualTo(2));
        Assert.That(Chunk(chunks, 1).Slice(1, 5).SequenceEqual((byte[])[97, 96, 0x60, 128, 0x52]));
        Assert.That(Chunk(chunks, 1)[6..].IsZero(), "padding must be zero");

        // PUSH32 data is capped at 31 bytes per chunk; the tail belongs to the next chunk.
        byte[] pushData = new byte[32];
        Array.Fill(pushData, (byte)0xAA);
        byte[] push32Code = [.. new byte[30], 0x7F, .. pushData];
        chunks = PbtKeyDerivation.ChunkifyCode(push32Code);
        Assert.That(chunks, Has.Length.EqualTo(3 * PbtKeyDerivation.CodeChunkSize));
        Assert.That(Chunk(chunks, 1)[0], Is.EqualTo(31), "a fully-PUSHDATA chunk is capped at 31");
        Assert.That(Chunk(chunks, 2)[0], Is.EqualTo(1), "one PUSHDATA byte remains in the last chunk");
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(31)]
    [TestCase(32)]
    [TestCase(63)]
    public void ChunkifyCodeClearsReusedDestination(int codeLength)
    {
        byte[] code = new byte[codeLength];
        byte[] chunks = new byte[(codeLength + 30) / 31 * PbtKeyDerivation.CodeChunkSize];
        chunks.AsSpan().Fill(0xFF);

        PbtKeyDerivation.ChunkifyCode(code, chunks);

        Assert.That(chunks.AsSpan().IsZero(), "opcode markers and padding must not retain pooled contents");
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
}
