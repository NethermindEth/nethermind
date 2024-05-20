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
        "909fa1ea85410c05c6aaafb798f89c33270509e61e0cf47e86c3fe86165391109d1be10208fba22ed22b13354d445bf4",
        "8fc1a5ba43a3a7e427ae907a6e9291f13f63f44ff6457f8558eaa31c0aa1b0d22320e296e79211c20633bf52306511a711162c799eaf0085c22f26f64e423f328711097f249192283efd74407817d96df8dc069f57ca5b38763e491d61ffed89",
        "3834a349678ef446bae07e2aeffc01054184af003834383438343834383438343834a349678ef446bae07e2aeffc01054184af00"
    )]
    public void Can_check_decryption_keys(string dkHex, string eonKeyHex, string identityPreimageHex)
    {
        G1 dk = new(Convert.FromHexString(dkHex));
        G2 eonKey = new(Convert.FromHexString(eonKeyHex));
        byte[] identityPreimage = Convert.FromHexString(identityPreimageHex);
        G1 identity = ShutterCrypto.ComputeIdentity(identityPreimage);

        Assert.That(ShutterCrypto.CheckDecryptionKey(dk, eonKey, identity));
    }

    [Test]
    [TestCase(
        "f869820248849502f900825208943834a349678ef446bae07e2aeffc01054184af008203e880824fd3a001e44318458b1f279bf81aef969df1b9991944bf8b9d16fd1799ed5b0a7986faa058f572cce63aaff3326df9c902d338b0c416c8fb93109446d6aadd5a65d3d115",
        "3834a349678eF446baE07e2AefFC01054184af00",
        "3834a349678eF446baE07e2AefFC01054184af00383438343834383438343834",
        "B068AD1BE382009AC2DCE123EC62DCA8337D6B93B909B3EE52E31CB9E4098D1B56D596BF3C08166C7B46CB3AA85C23381380055AB9F1A87786F2508F3E4CE5CAA5ABCDAE0A80141EE8CCC3626311E0A53BE5D873FA964FD85AD56771F2984579",
        "3834a349678eF446baE07e2AefFC01054184af00383438343834383438343834",
        "02B695A53BC2AB868E02786730030F78FA4CD3A24169966BCE28D6F2B2A73A8DAF9C1C57890CA24680DE84A175F67E4DD00E0FBC7531A017EBD4183E2C66B2726AA16C393A0D44BE40803EC1AFBE9F76BB0FD610E81E64760420008769E81799CB13EF2CFF94E7F4D809F4BC8B38599F940D25AC209A9661ED90A71562F3EC2CF18258DBDFF56ACC2F1A7C4E978C515A288BE09451EEE79B47E551F30F5B632C18FD43434574E0101FF74525CA254C1288AFB615B491A00452BD565F40DED22A8138F684DE2D21C26D2B48A439C3200FB4A172D76DBDE1228542FF3ABBF4EC09F1BFFAE3861F6CD187269FD1983CC9BB25122E37A2C21C33AD9590865B54EAA0B5"
    )]
    // cryptotests encrpytion 4
    // [TestCase(
    //     "5002552d0db819f6b6b95802607f00cc017dcf2b5a0384d105ebf694a3d89c52",
    //     "",
    //     "",
    //     "0097799f34f0470489fa4ea27e7b1ad0431567cda7b8e879d5272cd15a7d1fb97c7d90b73e72171ce2d2ebfbccc986c5104dea15f854c0c49d4cf0a75eca08912522d8ff5cca1c9940e70fec24ba45bacc7354abbab82f496bb8fb607ed4ba401081d51e6481af146e52cc47147c5dab6644f8c19d6701e9ae1d37f58d0ca2cfd134d141dea927d332ff253c08ee1418185629f285c589c8b8a063474adf697316769960026c488e62c3753d00d856be96a6f464eaa4a9074cfb1bd1706411e0",
    //     "71fabddbccb7ed674fb0b6035809e1296b48d3f21137d2159989d23c8e40a327",
    //     "0205549ea22448200ce3205a11089045ca5d74a8cab523f80e07b507843c82d6af35de963fbc6d6005b1d836d2a2e81db2171cd4cedab0bafa868d8d83447118a02101ef6fd2f3270c7a5d8a0302aaad0bb5b2d64ae479ad66adcc3fe373f1346c0d6936650d0df3edec2b30dd5a998c5a67957a0188c0d994886f851cf2633bcd60cc2e26c11a998b109a5d11722d8915002cc986b7fcbfd1c4132375024ecd5176356560684d5dd9076ec5f31d29d89fd0c2ec2ca8c1ee420275f0baaf0936e1d457230771ff7276793f67220d24316efd155d5720c658680e34859024de4676b3d083ff1f041451bec371dc80f4885a7fc34edd12704e2e0401590f8205e8ec2fe6a4b9cc421aef1cc305a740a0c5989a0b83b11dee4f60dd80c6b091bf90fc"
    // )]
    public void Can_encrypt(string rawTxHex, string senderAddress, string identityPrefixHex, string eonKeyHex, string sigmaHex, string expectedHex)
    {
        byte[] rawTx = Convert.FromHexString(rawTxHex);
        byte[] expected = Convert.FromHexString(expectedHex);

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
        Assert.That(encoded, Is.EqualTo(expected));
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
