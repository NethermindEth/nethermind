// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Crypto;
using NUnit.Framework;

namespace Nethermind.Core.Test.Crypto;

using G1 = Bls.P1;

[TestFixture]
public class BlsTests
{
    private static readonly byte[] SkBytes = [0x2c, 0xd4, 0xba, 0x40, 0x6b, 0x52, 0x24, 0x59, 0xd5, 0x7a, 0x0b, 0xed, 0x51, 0xa3, 0x97, 0x43, 0x5c, 0x0b, 0xb1, 0x1d, 0xd5, 0xf3, 0xca, 0x11, 0x52, 0xb3, 0x69, 0x4b, 0xb9, 0x1d, 0x7c, 0x22];
    private static readonly byte[] MsgBytes = [0x3e, 0x00, 0xef, 0x2f, 0x89, 0x5f, 0x40, 0xd6, 0x7f, 0x5b, 0xb8, 0xe8, 0x1f, 0x09, 0xa5, 0xa1, 0x2c, 0x84, 0x0e, 0xc3, 0xce, 0x9a, 0x7f, 0x3b, 0x18, 0x1b, 0xe1, 0x88, 0xef, 0x71, 0x1a, 0x1e];
    private static readonly int AggregateSignerCount = 100;

    [Test]
    public void Calculate_signature()
    {
        byte[] expected = [0xa5, 0xa0, 0x0d, 0xe9, 0x9d, 0x8f, 0xee, 0x7e, 0x28, 0x81, 0x1b, 0x2c, 0x08, 0xe0, 0xa7, 0xfc, 0x00, 0xa1, 0x10, 0x0c, 0x3d, 0x0f, 0x80, 0x51, 0x9d, 0x43, 0x24, 0x67, 0x1c, 0x29, 0x36, 0xb1, 0xe5, 0xa5, 0x87, 0x7d, 0x46, 0x7a, 0x6d, 0xc6, 0xf5, 0x92, 0xb2, 0x40, 0x7b, 0xcb, 0x12, 0x61, 0x0c, 0x18, 0x8a, 0x6c, 0xdf, 0x57, 0xd1, 0x77, 0x92, 0x00, 0x0f, 0xf7, 0x56, 0xf8, 0x0e, 0xbe, 0xd8, 0x00, 0x88, 0xab, 0x22, 0x9a, 0xa7, 0xe2, 0xc3, 0x24, 0x09, 0xec, 0xfe, 0x5a, 0x8d, 0x44, 0x73, 0xe9, 0x12, 0xfa, 0x19, 0x9e, 0xee, 0xa1, 0x8f, 0x3c, 0x79, 0x8d, 0xc5, 0x28, 0x64, 0x7d];
        Bls.SecretKey sk = new(SkBytes, Bls.ByteOrder.LittleEndian);
        BlsSigner.Signature s = BlsSigner.Sign(sk, MsgBytes);
        s.Bytes.ToArray().Should().Equal(expected);
    }

    [Test]
    public void Verify_signature()
    {
        Bls.SecretKey sk = new(SkBytes, Bls.ByteOrder.LittleEndian);
        BlsSigner.Signature s = BlsSigner.Sign(sk, MsgBytes);
        G1 publicKey = new();
        publicKey.FromSk(sk);
        Assert.That(BlsSigner.Verify(publicKey.ToAffine(), s, MsgBytes));
    }

    [Test]
    public void Verify_aggregate_signature()
    {
        BlsSigner.Signature agg = new();
        BlsSigner.Signature s = new();
        BlsSigner.AggregatedPublicKey aggregatedPublicKey = new();
        G1 pk = new();

        Bls.SecretKey masterSk = new(SkBytes, Bls.ByteOrder.LittleEndian);

        for (int i = 0; i < AggregateSignerCount; i++)
        {
            Bls.SecretKey sk = new(masterSk, (uint)i);
            s.Sign(sk, MsgBytes);
            agg.Aggregate(s);
            pk.FromSk(sk);
            aggregatedPublicKey.Aggregate(pk.ToAffine());
        }

        Assert.That(BlsSigner.VerifyAggregate(aggregatedPublicKey, agg, MsgBytes));
    }

    [Test]
    public void Rejects_bad_signature()
    {
        Bls.SecretKey sk = new(SkBytes, Bls.ByteOrder.LittleEndian);
        BlsSigner.Signature s = BlsSigner.Sign(sk, MsgBytes);
        Span<byte> badSig = stackalloc byte[96];
        s.Bytes.CopyTo(badSig);
        badSig[34] += 1;

        G1 publicKey = new();
        publicKey.FromSk(sk);
        Assert.That(BlsSigner.Verify(publicKey.ToAffine(), badSig, MsgBytes), Is.False);
    }

    [Test]
    public void Rejects_missing_aggregate_signature()
    {
        BlsSigner.Signature agg = new();
        BlsSigner.Signature s = new();
        BlsSigner.AggregatedPublicKey aggregatedPublicKey = new();
        G1 pk = new();

        Bls.SecretKey masterSk = new(SkBytes, Bls.ByteOrder.LittleEndian);

        for (int i = 0; i < AggregateSignerCount; i++)
        {
            Bls.SecretKey sk = new(masterSk, (uint)i);
            s.Sign(sk, MsgBytes);
            if (i != 0)
            {
                // exclude one signature
                agg.Aggregate(s);
            }
            pk.FromSk(sk);
            aggregatedPublicKey.Aggregate(pk.ToAffine());
        }

        Assert.That(BlsSigner.VerifyAggregate(aggregatedPublicKey, agg, MsgBytes), Is.False);
    }

    [Test]
    public void Public_key_from_private_key()
    {
        byte[] expected = [0x95, 0x39, 0x27, 0x35, 0x0c, 0x35, 0x31, 0xb0, 0xbc, 0x58, 0x64, 0xcd, 0x9c, 0x5f, 0xe1, 0x34, 0x74, 0xca, 0x0c, 0x9b, 0x59, 0x99, 0x51, 0xa7, 0x76, 0xc4, 0xb9, 0x8d, 0xf6, 0x6a, 0x0e, 0x62, 0x07, 0xa8, 0x5c, 0x7f, 0x7a, 0x85, 0x1a, 0x0c, 0x02, 0x2a, 0x87, 0xc0, 0x29, 0xc3, 0x65, 0x61];

        G1 publicKey = new();
        publicKey.FromSk(new(SkBytes, Bls.ByteOrder.LittleEndian));

        Assert.That(publicKey.Compress(), Is.EqualTo(expected));
    }
}
