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

    // cryptotests decryption 4
    [TestCase(
        "020f30e046988f04ad10c87154781bd56ecaa4c28781f8143d25ff68557a46fa7659647ac985c872ae71f0cd63b91afdb80f8715a8710f4732b904d7afff8a75f6258f56c3f3ae51da13b0ee0e89ffc4a7d583753753ad0791b8e3668e977bb5490a1ad08e20eb6b07edc8ea26225533be6156023014a3274f52ee3fa6b0cf4fbd02f3cbd8b4d99343762199645f36633008a76cbd77b6a448e3dc560377d78f86318b900ad41c10864c0a11efae653901d55795e3965702809ff45ef0893bffa7486deb2c34c9a9f83eb39d5df6e5ee5b93ddf150430882de1fbeaa2519b881f3234293d11237a5958361c6fe63a3ec4b10ff9dc965b8bd1a3c95e963a774d5d5",
        "07116abe6f491a3f72a18ea638d51922463b6019f2e8481350c3cc77f6fabfdb641bef476b5682870f0f22de44c82d3f141874bc0a7b93d8cb083e1fd88efa587804de410285120f15d47634271d88084aadca740ce2aa8c4cd1b3d5f2107a7b",
        // "13e185f4714a45250e7b4f16c940b2935d89fd52b719c7c2b50f35d11a1d0f079b67dddf90cbc9322fc2505ce127999b0641b950d4c2929db132dd44cce8e52793c1b82f1541d69db07be3796fefa8e05407c1611c303333b861074a983186f811e16948d3d99fc2cbf9ac07218edf0a7e7e3e9a67b08475f132dad4fd29b01ca0f97f231834c90b06e62ff6c97664eb10d7b895b581b4f69a530fd0e79581211ddec0ac5f562cc68b1e89416d0db5413ec7520352be7d842a210e8ed506a1f3152a0b5c8369404052148fcafdf3f53a2fdbd63cbd90b288ba104ca6561a4f642c65d2bd932c28f8c0c2930108012602166cbefb935dd1d93cd0c94d4d2aa4f91ddc0b8771b3121d7da4ff0bc36b9d989c0b3c3c123fcd27eb3ad17c0a5a3b0d048daef029e0c0760f6e8feb7384fef8f751dd4eb5d5a74fb3e0a958f0dda6f66d3985cf90264424b3330e58f6c2b532049d5e7feafe9a0c1e7293d583d07cf53f414cdbfd967e9ddf91aaa3c1a93599e39e93b8c6425e1b93b4a8c9f3faad9115e0615fd23a9954f405011b04839f1ec34208060d02a46cd18a8c367d572b835b2ab99ab79b45a4d96ac69c2219e706046a2c3c417c878f9ed84c590dae177289fe4ce57c5655c85642803689da460be9c738579e609b57b82a315e825c0d0815d9f9f15fb41ef8dd09bae7bf45df73acc9647c98d0bb263e684be1b6810d2a92448221a8c42053f056e3a2733eaf3b087e22bd11200e1533ade5f7f85cc75bbe6840e8b924d1efe4af2f29408c1d2f665af53729e2e72cce8f2d108ca515fd",
        "",
        "486deb2c34c9a9f83eb39d5df6e5ee5b93ddf150430882de1fbeaa2519b881f3",
        "68656c6c6f1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b",
        "68656c6c6f"
    )]
    public void Can_decrypt_data(string cipherTextHex, string decryptionKeyHex, string expectedGTHex, string expectedSigmaHex, string expectedBlockHex, string expectedHex)
    {
        EncryptedMessage c = ShutterCrypto.DecodeEncryptedMessage(Convert.FromHexString(cipherTextHex));
        G1 decryptionKey = new(Convert.FromHexString(decryptionKeyHex));

        // recover sigma

        // todo: change this when shutter swaps to blst
        // GT p = new(decryptionKey, c.c1);
        // byte[] gtBytes = p.final_exp().to_bendian();

        // TestContext.WriteLine("GT bytes: " + Convert.ToHexString(gtBytes));
        // Assert.That(gtBytes.SequenceEqual(Convert.FromHexString(expectedGTHex)));

        // Bytes32 key = ShutterCrypto.Hash2(gtBytes);
        Bytes32 key = new();
        Bytes32 sigma = ShutterCrypto.XorBlocks(c.c2, key);

        TestContext.WriteLine("recovered sigma: " + Convert.ToHexString(sigma.Unwrap()));
        Assert.That(sigma.Unwrap().SequenceEqual(Convert.FromHexString(expectedSigmaHex)));

        // decrypt
        IEnumerable<Bytes32> keys = ShutterCrypto.ComputeBlockKeys(sigma, c.c3.Count());
        IEnumerable<Bytes32> decryptedBlocks = Enumerable.Zip(keys, c.c3, ShutterCrypto.XorBlocks);
        TestContext.WriteLine("decrypted blocks: " + Convert.ToHexString(decryptedBlocks.First().Unwrap()));
        Assert.That(decryptedBlocks.First().Unwrap().SequenceEqual(Convert.FromHexString(expectedBlockHex)));

        byte[] decryptedMessage = ShutterCrypto.Decrypt(c, decryptionKey);
        TestContext.WriteLine("decrypted msg: " + Convert.ToHexString(decryptedMessage));

        Assert.That(decryptedMessage.SequenceEqual(Convert.FromHexString(expectedHex)));
    }

    [Test]
    [TestCase("", "")]
    public void Can_verify_validator_registration_signature(string msgHex, string sigHex)
    {
        Assert.That(true);
    }

    [Test]
    // [TestCase(60ul, 1ul, 10422348ul, 0ul, "0xcb770a9b31ac28b0c90d0357f8df7c1c1cd660be", "B684B1B441C79BC9E6A9F6454ABC849DF0FC068B9373C017AC443F3997A9D6BA433D29822BBEC4747D6A4119E022F1A5BC78495C496E22F2D8B1727746BA16B001", new string[] { })]
    [TestCase(
        7649174914161947266ul,
        16729082666370017565ul,
        8333205535599204084ul,
        17223499624376311426ul,
        "0x932552E9df00550E4c59fA4C233B440743e85974",
        "a9358a3e475e373d4749b9bce38df386e90b5b84742d77881448a6ce0db07e3077f8652d0133488962b7543b642c1025066904fb5c4278b91be6892b86c314c400",
        new string[] { "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f202122232425262728292a2b2c2d2e2f3031323334" },
        "0x6b634098ceabca795c8c23429fc2ad390448a826342bfc72e9996add66ecd205"
    )]
    public void Can_verify_decryption_key_signatures(ulong instanceId, ulong eon, ulong slot, ulong txPointer, string keyperAddress, string sigHex, string[] identityPreimagesHex, string expectedHash)
    {
        List<byte[]> identityPreimages = identityPreimagesHex.Select(Convert.FromHexString).ToList();
        Assert.That(ShutterCrypto.GenerateHash(instanceId, eon, slot, txPointer, identityPreimages).ToString(), Is.EqualTo(expectedHash));
        Assert.That(ShutterCrypto.CheckSlotDecryptionIdentitiesSignature(instanceId, eon, slot, txPointer, identityPreimages, Convert.FromHexString(sigHex), new(keyperAddress)));
    }

    internal static EncryptedMessage Encrypt(ReadOnlySpan<byte> msg, G1 identity, G2 eonKey, Bytes32 sigma)
    {
        UInt256 r;
        ShutterCrypto.ComputeR(sigma, msg, out r);

        EncryptedMessage c = new()
        {
            VersionId = ShutterCrypto.CryptoVersion,
            c1 = ShutterCrypto.ComputeC1(r),
            c2 = ComputeC2(sigma, r, identity, eonKey),
            c3 = ComputeC3(PadAndSplit(msg), sigma)
        };
        return c;
    }

    internal static byte[] EncodeEncryptedMessage(EncryptedMessage encryptedMessage)
    {
        byte[] bytes = new byte[1 + 192 + 32 + (encryptedMessage.c3.Count() * 32)];

        bytes[0] = encryptedMessage.VersionId;
        encryptedMessage.c1.serialize().CopyTo(bytes.AsSpan()[1..]);
        encryptedMessage.c2.Unwrap().CopyTo(bytes.AsSpan()[(1 + 192)..]);

        foreach ((Bytes32 block, int i) in encryptedMessage.c3.WithIndex())
        {
            int offset = 1 + 192 + 32 + (32 * i);
            block.Unwrap().CopyTo(bytes.AsSpan()[offset..]);
        }

        return bytes;
    }

    private static Bytes32 ComputeC2(Bytes32 sigma, UInt256 r, G1 identity, G2 eonKey)
    {
        // todo: change once shutter changes to blst
        // GT p = new(identity, eonKey);
        // GT preimage = ShutterCrypto.GTExp(p, r);
        // Bytes32 key = ShutterCrypto.Hash2(preimage);
        Bytes32 key = new();
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
