// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Crypto.PairingCurves;
using Nethermind.Int256;
using Nethermind.Merge.AuRa.Shutter;
using NUnit.Framework;

namespace Nethermind.Merge.AuRa.Test;

using G1 = BlsCurve.G1;
using G2 = BlsCurve.G2;
using GT = BlsCurve.GT;
using EncryptedMessage = ShutterCrypto.EncryptedMessage;

class ShutterCryptoTests
{
    [SetUp]
    public void SetUp()
    {

    }

    [Test]
    public void Can_decrypt()
    {
        byte[] msg = [0xca, 0x55, 0x72, 0x15, 0xff, 0x44];
        UInt256 sk = 123456789;
        G1 identity = G1.FromScalar(3261443);
        G2 eonKey = G2.FromScalar(sk);
        Bytes32 sigma = new([0x12, 0x15, 0xaa, 0xbb, 0x33, 0xfd, 0x66, 0x55, 0x15, 0xaa, 0xbb, 0x33, 0xfd, 0x66, 0x55, 0x15, 0xaa, 0xbb, 0x33, 0xfd, 0x66, 0x55, 0x15, 0xaa, 0xbb, 0x33, 0xfd, 0x66, 0x55, 0x22, 0x88, 0x45]);
        EncryptedMessage c = Encrypt(msg, identity, eonKey, sigma);
        G1 key = sk * identity;
        byte[] res = ShutterCrypto.Decrypt(c, key);
        Assert.That(res, Is.EqualTo(msg));
    }

    private static EncryptedMessage Encrypt(ReadOnlySpan<byte> msg, G1 identity, G2 eonKey, Bytes32 sigma)
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

    private static Bytes32 ComputeC2(Bytes32 sigma, UInt256 r, G1 identity, G2 eonKey)
    {
        GT p = BlsCurve.Pairing(identity, eonKey);
        GT preimage = r * p;
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
