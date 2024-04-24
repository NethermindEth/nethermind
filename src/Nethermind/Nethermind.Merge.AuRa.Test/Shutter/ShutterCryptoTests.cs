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
using Nethermind.Serialization.Rlp;
using Nethermind.Core.Test;

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
        Bytes32 h1 = ShutterCrypto.Hash2(p1);
        GT p2 = ShutterCrypto.GTExp(new GT(identity, eonKey), r);
        Bytes32 h2 = ShutterCrypto.Hash2(p2);

        Assert.That(h1, Is.EqualTo(h2));
    }

    [Test]
    [TestCase("f869820243849502f900825208943834a349678ef446bae07e2aeffc01054184af008203e880824fd4a0510c063afbe5b8b8875b680e96a1778c99c765cc0df263f10f8d9707cfa0f114a02590b2ce6dbce6532da17c52a2a7f2eb6155f23404128fca5fb72dc852ce64c6")]
    [TestCase("08825208943834a349678ef446bae07e2aeffc01054184af008203e880824fd4a02356f869820246849502f900825208943834a349678ef446bae07e2aeffc01054184af008203e880824fd4a02356b138904ed89a72a1fa913aa651c3b4144a5b47aa0cbf6a6cf9956d896bc0a0825208943834a349678ef446bae07e2aeffc01054184af008203e880824fd4a023560825208943834a349678ef446bae07e2aeffc01054184af008203e880824fd4a0235607e1364d24a98ac1cdb3f0af8c5c0cf164528df11dd766aa368d4136651ceb55e")]
    public void Can_encrypt_then_decrypt(string msgHex)
    {
        byte[] msg = Convert.FromHexString(msgHex);
        UInt256 sk = 123456789;
        G1 identity = G1.generator().mult(3261443);
        G2 eonKey = G2.generator().mult(sk.ToLittleEndian());
        Bytes32 sigma = new([0x12, 0x15, 0xaa, 0xbb, 0x33, 0xfd, 0x66, 0x55, 0x15, 0xaa, 0xbb, 0x33, 0xfd, 0x66, 0x55, 0x15, 0xaa, 0xbb, 0x33, 0xfd, 0x66, 0x55, 0x15, 0xaa, 0xbb, 0x33, 0xfd, 0x66, 0x55, 0x22, 0x88, 0x45]);

        TestContext.WriteLine("eon key for " + sk + ": " + Convert.ToHexString(eonKey.compress()));

        EncryptedMessage encryptedMessage = Encrypt(msg, identity, eonKey, sigma);
        G1 key = identity.dup().mult(sk.ToLittleEndian());

        Assert.That(ShutterCrypto.RecoverSigma(encryptedMessage, key), Is.EqualTo(sigma));
        Assert.That(msg.SequenceEqual(ShutterCrypto.Decrypt(encryptedMessage, key)));

        var decoded = ShutterCrypto.DecodeEncryptedMessage(EncodeEncryptedMessage(encryptedMessage));
        Assert.That(encryptedMessage.c1.is_equal(decoded.c1));
        Assert.That(encryptedMessage.c2, Is.EqualTo(decoded.c2));
        Assert.That(encryptedMessage.c3, Is.EqualTo(decoded.c3));
    }

    [Test]
    [TestCase(
        "f869820248849502f900825208943834a349678ef446bae07e2aeffc01054184af008203e880824fd3a001e44318458b1f279bf81aef969df1b9991944bf8b9d16fd1799ed5b0a7986faa058f572cce63aaff3326df9c902d338b0c416c8fb93109446d6aadd5a65d3d115",
        "3834a349678eF446baE07e2AefFC01054184af00",
        "3834a349678eF446baE07e2AefFC01054184af00383438343834383438343834",
        "B068AD1BE382009AC2DCE123EC62DCA8337D6B93B909B3EE52E31CB9E4098D1B56D596BF3C08166C7B46CB3AA85C23381380055AB9F1A87786F2508F3E4CE5CAA5ABCDAE0A80141EE8CCC3626311E0A53BE5D873FA964FD85AD56771F2984579",
        "3834a349678eF446baE07e2AefFC01054184af00383438343834383438343834"
    )]
    public void Output_encrypted_transaction(string rawTxHex, string senderAddress, string identityPrefixHex, string eonKeyHex, string sigmaHex)
    {
        byte[] rawTx = Convert.FromHexString(rawTxHex);

        Transaction transaction = Rlp.Decode<Transaction>(new Rlp(rawTx));
        transaction.SenderAddress = new EthereumEcdsa(BlockchainIds.Chiado, new NUnitLogManager()).RecoverAddress(transaction, true);
        TestContext.WriteLine(transaction.ToShortString());

        Bytes32 identityPrefix = new(Convert.FromHexString(identityPrefixHex).AsSpan());
        G1 identity = ShutterCrypto.ComputeIdentity(identityPrefix, new(senderAddress));
        G2 eonKey = new(Convert.FromHexString(eonKeyHex));
        Bytes32 sigma = new(Convert.FromHexString(sigmaHex).AsSpan());

        EncryptedMessage c = Encrypt(rawTx, identity, eonKey, sigma);

        byte[] encoded = EncodeEncryptedMessage(c);
        TestContext.WriteLine("encrypted tx: " + Convert.ToHexString(encoded));
    }

    internal static EncryptedMessage Encrypt(ReadOnlySpan<byte> msg, G1 identity, G2 eonKey, Bytes32 sigma)
    {
        UInt256 r;
        ShutterCrypto.ComputeR(sigma, msg, out r);

        EncryptedMessage c = new()
        {
            VersionId = 0x2,
            c1 = ShutterCrypto.ComputeC1(r),
            c2 = ComputeC2(sigma, r, identity, eonKey),
            c3 = ComputeC3(PadAndSplit(msg), sigma)
        };
        return c;
    }

    internal static byte[] EncodeEncryptedMessage(EncryptedMessage encryptedMessage)
    {
        byte[] bytes = new byte[1 + 96 + 32 + (encryptedMessage.c3.Count() * 32)];

        bytes[0] = encryptedMessage.VersionId;
        encryptedMessage.c1.compress().CopyTo(bytes.AsSpan()[1..]);
        encryptedMessage.c2.Unwrap().CopyTo(bytes.AsSpan()[(1 + 96)..]);

        foreach ((Bytes32 block, int i) in encryptedMessage.c3.WithIndex())
        {
            int offset = 1 + 96 + 32 + (32 * i);
            block.Unwrap().CopyTo(bytes.AsSpan()[offset..]);
        }

        return bytes;
    }

    private static Bytes32 ComputeC2(Bytes32 sigma, UInt256 r, G1 identity, G2 eonKey)
    {
        GT p = new(identity, eonKey);
        GT preimage = ShutterCrypto.GTExp(p, r);
        Bytes32 key = ShutterCrypto.Hash2(preimage);
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

        for (int i = 0; i < padded.Length / 32; i++)
        {
            int offset = i * 32;
            res.Add(new(padded[offset..(offset + 32)]));
        }
        return res;
    }
}
