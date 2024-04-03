// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Merge.AuRa.Shutter;
using NUnit.Framework;
using Nethermind.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Merge.AuRa.Test;

using G1 = Bls.P1;
using G2 = Bls.P2;
using GT = Bls.PT;
using EncryptedMessage = ShutterCrypto.EncryptedMessage;

class ShutterCryptoTests
{
    [Test]
    public void Pairing_holds()
    {
        UInt256 sk = 123456789;
        UInt256 r = 4444444444;
        G1 identity = G1.generator().mult(3261443);
        G2 eonKey = G2.generator().mult(sk.ToLittleEndian());
        G1 key = identity.dup().mult(sk.ToLittleEndian());

        GT p1 = new(key, G2.generator().mult(r.ToLittleEndian()));
        Bytes32 h1 = ShutterCrypto.HashGTToBlock(p1);
        GT p2 = ShutterCrypto.GTExp(new GT(identity, eonKey), r);
        Bytes32 h2 = ShutterCrypto.HashGTToBlock(p2);

        Assert.That(h1, Is.EqualTo(h2));
    }

    [Test]
    public void Can_decrypt()
    {
        Span<byte> msg = stackalloc byte[200];
        msg.Fill(55);
        byte[] b = [0xca, 0x55, 0x72, 0x15, 0xff, 0x44];
        b.CopyTo(msg);

        UInt256 sk = 123456789;
        G1 identity = G1.generator().mult(3261443);
        G2 eonKey = G2.generator().mult(sk.ToLittleEndian());
        Bytes32 sigma = new([0x12, 0x15, 0xaa, 0xbb, 0x33, 0xfd, 0x66, 0x55, 0x15, 0xaa, 0xbb, 0x33, 0xfd, 0x66, 0x55, 0x15, 0xaa, 0xbb, 0x33, 0xfd, 0x66, 0x55, 0x15, 0xaa, 0xbb, 0x33, 0xfd, 0x66, 0x55, 0x22, 0x88, 0x45]);
        EncryptedMessage c = Encrypt(msg, identity, eonKey, sigma);

        G1 key = identity.dup().mult(sk.ToLittleEndian());
        Assert.That(ShutterCrypto.RecoverSigma(c, key), Is.EqualTo(sigma));

        Span<byte> res = ShutterCrypto.Decrypt(c, key);
        Assert.That(res.SequenceEqual(msg));
    }

    [Test]
    public void Can_encrypt()
    {
        Span<byte> msg = Convert.FromHexString("f86a8201f88504a817c800825208943834a349678ef446bae07e2aeffc01054184af008203e880824fd4a010295a68cfc27a6647131a1cf6477bf43e615973f0b7e529bcef2dddf0b895f3a05a64529e0f44b2621e87c11ca9a2d638f3fa7994a8b043beed056d60e5608732");

        Bytes32 identityPrefix = new([0x23, 0xbb, 0xdd, 0x06, 0x95, 0xf3, 0x66, 0x55, 0x15, 0xaa, 0xbb, 0x33, 0xfd, 0x66, 0x55, 0x15, 0xaa, 0xbb, 0x33, 0xfd, 0x66, 0x55, 0x15, 0xaa, 0xbb, 0x33, 0xfd, 0x66, 0x55, 0x22, 0x88, 0x45]);
        Address sender = new("3834a349678eF446baE07e2AefFC01054184af00");
        G1 identity = ShutterCrypto.ComputeIdentity(identityPrefix, sender);

        UInt256 sk = 123456789;
        G2 eonKey = G2.generator().mult(sk.ToLittleEndian());

        Bytes32 sigma = new([0x12, 0x15, 0xaa, 0xbb, 0x33, 0xfd, 0x66, 0x55, 0x15, 0xaa, 0xbb, 0x33, 0xfd, 0x66, 0x55, 0x15, 0xaa, 0xbb, 0x33, 0xfd, 0x66, 0x55, 0x15, 0xaa, 0xbb, 0x33, 0xfd, 0x66, 0x55, 0x22, 0x88, 0x45]);

        EncryptedMessage c = Encrypt(msg, identity, eonKey, sigma);

        byte[] encoded = EncodeEncryptedMessage(c);
        TestContext.WriteLine("encrypted tx: " + Convert.ToHexString(encoded));
        TestContext.WriteLine("identity prefix: " + Convert.ToHexString(identityPrefix.Unwrap()));
        TestContext.WriteLine("eon key: " + Convert.ToHexString(eonKey.compress()));
    }

    internal static EncryptedMessage Encrypt(ReadOnlySpan<byte> msg, G1 identity, G2 eonKey, Bytes32 sigma)
    {
        UInt256 r = ShutterCrypto.ComputeR(sigma, msg);
        EncryptedMessage c = new()
        {
            c1 = ShutterCrypto.ComputeC1(r),
            c2 = ComputeC2(sigma, r, identity, eonKey),
            c3 = ComputeC3(PadAndSplit(msg), sigma)
        };
        return c;
    }

    internal static byte[] EncodeEncryptedMessage(EncryptedMessage encryptedMessage)
    {
        byte[] bytes = new byte[96 + 32 + (encryptedMessage.c3.Count() * 32)];

        encryptedMessage.c1.compress().CopyTo(bytes.AsSpan());
        encryptedMessage.c2.Unwrap().CopyTo(bytes.AsSpan()[96..]);

        foreach ((Bytes32 block, int i) in encryptedMessage.c3.WithIndex())
        {
            int offset = 96 + 32 + (32 * i);
            block.Unwrap().CopyTo(bytes.AsSpan()[offset..]);
        }

        return bytes;
    }

    private static Bytes32 ComputeC2(Bytes32 sigma, UInt256 r, G1 identity, G2 eonKey)
    {
        GT p = new(identity, eonKey);
        GT preimage = ShutterCrypto.GTExp(p, r);
        Bytes32 key = ShutterCrypto.HashGTToBlock(preimage);
        return ShutterCrypto.XorBlocks(sigma, key);
    }

    private static IEnumerable<Bytes32> ComputeC3(IEnumerable<Bytes32> messageBlocks, Bytes32 sigma)
    {
        IEnumerable<Bytes32> keys = ShutterCrypto.ComputeBlockKeys(sigma, messageBlocks.Count());
        return Enumerable.Zip(keys, messageBlocks, ShutterCrypto.XorBlocks);
    }

    private static IEnumerable<Bytes32> PadAndSplit(ReadOnlySpan<byte> bytes)
    {
        List<Bytes32> res = [];
        int n = 32 - (bytes.Length % 32);
        Span<byte> padded = stackalloc byte[bytes.Length + n];
        padded.Fill((byte)n);
        bytes.CopyTo(padded);
        for (int i = 0; i < (bytes.Length + n) / 32; i++)
        {
            res.Add(new(padded[(i * 32)..((i + 1) * 32)]));
        }
        return res;
    }
}
