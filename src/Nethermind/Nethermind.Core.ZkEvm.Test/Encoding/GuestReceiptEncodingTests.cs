// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.ZkEvm.Test.Encoding;

/// <summary>
/// Covers the guest's one-pass encoding of receipts for the receipts root, which the host suite cannot reach.
/// </summary>
/// <remarks>The logs, data and gas reach every prefix width.</remarks>
public class GuestReceiptEncodingTests
{
    private static readonly ReceiptMessageDecoder Decoder = new();

    [Test]
    public void One_pass_encoding_matches_the_generic_one(
        [Values(TxType.Legacy, TxType.EIP1559, TxType.SetCode)] TxType type,
        [Values] bool eip7668,
        [Range(0, 7)] int seed)
    {
        Random random = new(seed * 31 + (int)type);
        RlpBehaviors trie = RlpBehaviors.Eip658Receipts | RlpBehaviors.SkipTypedWrapping |
            (eip7668 ? RlpBehaviors.Eip7668Receipts : RlpBehaviors.None);
        for (int i = 0; i < 40; i++)
        {
            TxReceipt receipt = CreateReceipt(random, type);
            Assert.That(Decoder.EncodeNew(receipt, trie), Is.EqualTo(EncodeGeneric(receipt, trie)));
        }
    }

    /// <remarks>
    /// The one-pass encoding takes only an <see cref="RlpWriter"/>, so a byte-buffer writer gets the generic encoding,
    /// which measures neither through the one-pass length nor into a buffer sized by it.
    /// </remarks>
    private static byte[] EncodeGeneric(TxReceipt receipt, RlpBehaviors behaviors)
    {
        IByteBuffer buffer = Unpooled.Buffer();
        try
        {
            ByteBufferRlpWriter writer = new(buffer);
            Decoder.PopulateAndEncode(ref writer, receipt, behaviors);
            return buffer.ReadAllBytesAsArray();
        }
        finally
        {
            buffer.Release();
        }
    }

    private static TxReceipt CreateReceipt(Random random, TxType type)
    {
        LogEntry[] logs = new LogEntry[random.Next(4) == 0 ? random.Next(20, 70) : random.Next(0, 4)];
        for (int i = 0; i < logs.Length; i++)
        {
            Hash256[] topics = new Hash256[random.Next(0, 5)];
            for (int t = 0; t < topics.Length; t++) topics[t] = new Hash256(RandomBytes(random, Hash256.Size));
            logs[i] = new LogEntry(new Address(RandomBytes(random, Address.Size)), RandomData(random), topics);
        }

        byte[] bloom = RandomBytes(random, Bloom.ByteLength);
        return new TxReceipt
        {
            TxType = type,
            StatusCode = (byte)random.Next(2),
            GasUsedTotal = random.Next(6) switch
            {
                0 => 0,
                1 => (ulong)random.Next(1, 128),
                2 => 128,
                3 => ulong.MaxValue,
                _ => (ulong)random.NextInt64() >> random.Next(64),
            },
            Bloom = random.Next(4) == 0 ? Bloom.Empty : new Bloom(bloom),
            Logs = logs,
        };
    }

    private static byte[] RandomData(Random random)
    {
        byte[] data = RandomBytes(random, random.Next(9) switch
        {
            0 => 0,
            1 or 2 => 1,
            3 => 55,
            4 => 56,
            5 => 300,
            6 => 70_000,
            _ => random.Next(2, 200),
        });
        // A single byte below 0x80 encodes as itself.
        if (data.Length == 1 && random.Next(2) == 0) data[0] &= 0x7f;
        return data;
    }

    private static byte[] RandomBytes(Random random, int length)
    {
        byte[] bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }
}
