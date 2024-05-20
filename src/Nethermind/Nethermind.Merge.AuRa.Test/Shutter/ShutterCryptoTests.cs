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
    public void Can_encrypt_transaction(string rawTxHex, string senderAddress, string identityPrefixHex, string eonKeyHex, string sigmaHex, string expectedHex)
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

    // cryptotests encryption 4
    [TestCase(
        "b2090af6cd6ae2f7a16b4331a3328beea23f18c06254ed653145d39fd8484f09",
        "1ae8ba18ae8995aaa42a90fcccdbbff7469bcfd21654187466d27d7214f51bab8437e6b559ee5f110bd4ea13c93c94f781f7d2d9",
        "0f23dd91901e0b29be6b082fc42e5a2b99a8ff7d3b547cd62c57ab761770ba7301563eff95b052381e985a7df962efa60fae52cf83055a415b80b060c6a9c4c0c86a617ef71fe1f31471f411ab9caad3fdf2ba8239acc8935fc4cbd70910190d0619fd855c8c8d5668e14c255fbc17dfb43ba6becb81c124d2b0fa0260c810b2b15c80ff715f2526301188b52e6900b5047b87a30fe80f8801c0a02a0fb6c1e8e807cd28d1caaeeeee10d64dcb1f260cb430c1f06792d1d8e9dced4634bcc9c9",
        "5a6d3b31ae1435bacfe58b671e4143bdb9192c70ff1fa67625d9d6f8fdbcb858",
        "02187977b02f9e6b80fe71e16b5b70570ef2940b8b876b9ded75fc8985bf7682d77fb1267252ba7b50fc6a9ba10bf2069802378c92904a1135d3426a792d0779a7bca89bfb3ceda1ba8c7e44efbf559e0611117944f99876036ceedd7af9cbfcaf09962627cee97096d1922f1e0bfef3d9db625358533a961b6c5d323b7136e2f87cceea36d50dc91c2ae11cc4dfaa28e60dcbb3d8108f82fa3af0f92ec85d0e4a94187b9d65665999566a920fa62a67fafae32ad7f0209b2a41af126fc73a07ac44ef6413e4dd02d0a968c651f14c53fa55875cdd20b349dd09acdd701d24077b7f74873db95a89b10a106dcb1ddb764998749d993d06fa8626de936e53609b4212da39d7944907d356c08b80e0e00201ce1f3e887269f0e71e408c9741a12d1c"
    )]
    public void Can_encrypt_data(string msgHex, string identityPreimageHex, string eonKeyHex, string sigmaHex, string expectedHex)
    {
        byte[] msg = Convert.FromHexString(msgHex);
        byte[] expected = Convert.FromHexString(expectedHex);

        G1 identity = ShutterCrypto.ComputeIdentity(Convert.FromHexString(identityPreimageHex));
        G2 eonKey = new(Convert.FromHexString(eonKeyHex));
        Bytes32 sigma = new(Convert.FromHexString(sigmaHex).AsSpan());

        EncryptedMessage c = Encrypt(msg, identity, eonKey, sigma);

        byte[] encoded = EncodeEncryptedMessage(c);
        TestContext.WriteLine("encrypted msg: " + Convert.ToHexString(encoded));
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
