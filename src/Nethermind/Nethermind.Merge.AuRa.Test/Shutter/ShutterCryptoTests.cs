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

        EncryptedMessage encryptedMessage = ShutterCrypto.Encrypt(msg, identity, eonKey, sigma);
        G1 key = identity.dup().mult(sk.ToLittleEndian());

        Assert.That(ShutterCrypto.RecoverSigma(encryptedMessage, key), Is.EqualTo(sigma));
        Assert.That(msg.SequenceEqual(ShutterCrypto.Decrypt(encryptedMessage, key)));

        var decoded = ShutterCrypto.DecodeEncryptedMessage(ShutterCrypto.EncodeEncryptedMessage(encryptedMessage));
        Assert.That(encryptedMessage.C1.is_equal(decoded.C1));
        Assert.That(encryptedMessage.C2, Is.EqualTo(decoded.C2));
        Assert.That(encryptedMessage.C3, Is.EqualTo(decoded.C3));
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
    // [TestCase(
    //     "f869820248849502f900825208943834a349678ef446bae07e2aeffc01054184af008203e880824fd3a001e44318458b1f279bf81aef969df1b9991944bf8b9d16fd1799ed5b0a7986faa058f572cce63aaff3326df9c902d338b0c416c8fb93109446d6aadd5a65d3d115",
    //     "3834a349678eF446baE07e2AefFC01054184af00",
    //     "3834a349678eF446baE07e2AefFC01054184af00383438343834383438343834",
    //     "B068AD1BE382009AC2DCE123EC62DCA8337D6B93B909B3EE52E31CB9E4098D1B56D596BF3C08166C7B46CB3AA85C23381380055AB9F1A87786F2508F3E4CE5CAA5ABCDAE0A80141EE8CCC3626311E0A53BE5D873FA964FD85AD56771F2984579",
    //     "3834a349678eF446baE07e2AefFC01054184af00383438343834383438343834",
    //     "02B695A53BC2AB868E02786730030F78FA4CD3A24169966BCE28D6F2B2A73A8DAF9C1C57890CA24680DE84A175F67E4DD00E0FBC7531A017EBD4183E2C66B2726AA16C393A0D44BE40803EC1AFBE9F76BB0FD610E81E64760420008769E81799CB13EF2CFF94E7F4D809F4BC8B38599F940D25AC209A9661ED90A71562F3EC2CF18258DBDFF56ACC2F1A7C4E978C515A288BE09451EEE79B47E551F30F5B632C18FD43434574E0101FF74525CA254C1288AFB615B491A00452BD565F40DED22A8138F684DE2D21C26D2B48A439C3200FB4A172D76DBDE1228542FF3ABBF4EC09F1BFFAE3861F6CD187269FD1983CC9BB25122E37A2C21C33AD9590865B54EAA0B5"
    // )]
    [TestCase(
        "f869820a58849502f900825208943834a349678ef446bae07e2aeffc01054184af008203e880824fd4a0c3e550e1678adc8631ed77dd8b0de9d553cdb870490dfba468f7951fe968c48ca05350751ce7d98009309a1b572df956a5a1cd61f31a83546e2d1e8a9bef6fa062",
        "3834a349678eF446baE07e2AefFC01054184af00",
        "3834a349678eF446baE07e2AefFC01054184af00383438343834383438343834",
        "8a70247b414a44d0f08ec1d08484e1192fe68f7e08c3f4f1bf6acefa668de84004e3ed5f6dc8b66087b177fcd0a20a6a16430893464a8d69666c6f28c7b4d1da67b2e09a2b64d719b6b61459feb38e7d8bb13da435845b35fb89f7bbe878e2fa",
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

        EncryptedMessage c = ShutterCrypto.Encrypt(rawTx, identity, eonKey, sigma);

        byte[] encoded = ShutterCrypto.EncodeEncryptedMessage(c);
        TestContext.WriteLine("encrypted tx: " + Convert.ToHexString(encoded));
        Assert.That(encoded, Is.EqualTo(expected));
    }

    [Test]
    // encryption 4
    [TestCase(
        "f12853fff4e9d2e997039e317f789e1684c4c8766cfce77a3153321601091079",
        "4317056f139d3e14187cd2af6b2130eb9ef5f3ce9c50247794ba2079b4d171e8a6aefd94c7fbe399c78f8e54df719c9b9bfd6b33",
        "a3a4f2a3872a181c94319e2d48a34c5c0e0c6e554ad00d28952296a8e1f526928ee5e4f9449a396eed3a8361d3f502a61622acfc955b85fc7c80df6fc81bd02e10d69ef1e87c3b2d53f9305639959851b2ae0a29f4987f53c21879b6ebb61fd6",
        "00fd9f0a50cb900738382372f3ffbd960983b032aa5208d6afe237a9bf175a47",
        "0290633abedfed746bd1a571f0b90da2fb66d6ce2f1ff36197095fdb6ac1a354694d01f2437a44a9d3a733a772ccc502e71769f538451cc5e048e649b77adda9c3adb3689d3869364d99151286f9ca5db57083cb5f37c6179b409e5ea1ecdff55a4d02ef4c0e48e9a35f0c818c15d5cb5166a29905ab68621a5042b42993e82d75a0d387431f41e354342f80c7d8079aceb3898357f0d6336eee3a6e057863116fe8bd47c38df1667709579bf3eedc8f3fbc7dc6cf099135eb364db97d551a29b1"
    )]
    // encryption 9
    [TestCase(
        "41206d657373616765",
        "aab9d584f806604ede9a08901ef2cf2c0662e55ff8336e308177530d0a00ca9863665711a4e81ee16d747cca60c7e5aea8fdf4c5",
        "b7fa7f731d23f3143278b62e41c98e86f89d94fd958566f93fae4f60ab3ec9d9c6d971509a602fb64fc6ff8ec48b6253130df3cd1565ad67d7e6842fe66b512b67e0c444a8c1d2878f9baccaf1b1d54e2d220a829b573f19b5f27f23ce23b61d",
        "62ec54c52c5afa2e225da1a583d36d8e6076119db05c8ca2db2333327759c070",
        "02976ecaed3c2473cd80d811c0afef4855accc436f51f3aaae09244af6d3705d72597bb4c8ca4cc63cb320c1bfb6eab9c5167e9f7b240540baf37f860b79f61e23412732abede6daf0752b6a24a9aee2f0e3e20dc6f4b2bd2ae2cfc82bd2c20f164c16331cedda8e60d472cb64441ded562f755775bb5201c2e73ff98b1928e5b7ca2b954f3b0f799f7904295f98b2ba194b93aa4c637ea9ba1695b868e9ed0931"
    )]
    // encryption 2
    [TestCase(
        "dc",
        "028a32bc9d9c4b8c2b63df424afbf545b30b6550b9718a6ef22e702f4e6473c40c315b1ab2b2ee41db15b4f3e365d934c62b991d",
        "98b79f256c1945c4bfb43f625d1c91ec176cbebe78e72d466dcbf282e2bc4a014abe66d333bfd06f82b6f4e1fa5b6184096dee94195df23ab7bd37b518b1313d8b46d02541a9591608cb9b11256aba222bc2233f97b60439c3dfc9ae7f96a51e",
        "ebb6d602c2116e88cc88d14c4e1c97ace16c4479027707fb5dfdbacfece4fcb1",
        "02ad644e5e3d095829d4f8a9b44d679887b922a78b783667522a12b18ad994f773ac06c3b47a4389efdbb9a6a6e97ff3701613d8e9e3a0d9defd520b353263bc73a15ee9b4729b59f6856bfa36bb1aff259d3df281749908cba45cdb8cba8555674ba8c0b3f617888d4e5fa647edff4f9888c42ef83537b804ee3ece3894bf8b608561ab85e78f093dc13de48a1091863b105e8f053dbe87efb72ad1aa115c4678"
    )]
    public void Can_encrypt_data(string msgHex, string identityPreimageHex, string eonKeyHex, string sigmaHex, string expectedHex)
    {
        byte[] msg = Convert.FromHexString(msgHex);
        byte[] expected = Convert.FromHexString(expectedHex);

        G1 identity = ShutterCrypto.ComputeIdentity(Convert.FromHexString(identityPreimageHex));
        G2 eonKey = new(Convert.FromHexString(eonKeyHex));
        Bytes32 sigma = new(Convert.FromHexString(sigmaHex).AsSpan());

        EncryptedMessage c = ShutterCrypto.Encrypt(msg, identity, eonKey, sigma);

        byte[] encoded = ShutterCrypto.EncodeEncryptedMessage(c);
        TestContext.WriteLine("encrypted msg: " + Convert.ToHexString(encoded));
        Assert.That(encoded, Is.EqualTo(expected));
    }

    // cryptotests decryption 4
    [Test]
    [TestCase(
        "0290633abedfed746bd1a571f0b90da2fb66d6ce2f1ff36197095fdb6ac1a354694d01f2437a44a9d3a733a772ccc502e71769f538451cc5e048e649b77adda9c3adb3689d3869364d99151286f9ca5db57083cb5f37c6179b409e5ea1ecdff55a4d02ef4c0e48e9a35f0c818c15d5cb5166a29905ab68621a5042b42993e82d75a0d387431f41e354342f80c7d8079aceb3898357f0d6336eee3a6e057863116fe8bd47c38df1667709579bf3eedc8f3fbc7dc6cf099135eb364db97d551a29b1",
        "89301819c6d24ef7555d822d6ad3543fdf28ecc149ba1c4359527c406fd700a75db6025d07010dc35a440da372b1318a",
        "f12853fff4e9d2e997039e317f789e1684c4c8766cfce77a3153321601091079"
    )]
    public void Can_decrypt_data(string cipherTextHex, string decryptionKeyHex, string expectedHex)
    {
        EncryptedMessage c = ShutterCrypto.DecodeEncryptedMessage(Convert.FromHexString(cipherTextHex));
        G1 decryptionKey = new(Convert.FromHexString(decryptionKeyHex));

        // recover sigma
        GT p = new(decryptionKey, c.C1);
        Bytes32 key = ShutterCrypto.Hash2(p);
        Bytes32 sigma = ShutterCrypto.XorBlocks(c.C2, key);

        // decrypt
        IEnumerable<Bytes32> keys = ShutterCrypto.ComputeBlockKeys(sigma, c.C3.Count());
        IEnumerable<Bytes32> decryptedBlocks = Enumerable.Zip(keys, c.C3, ShutterCrypto.XorBlocks);

        byte[] decryptedMessage = ShutterCrypto.Decrypt(c, decryptionKey);
        TestContext.WriteLine("decrypted msg: " + Convert.ToHexString(decryptedMessage));

        Assert.That(decryptedMessage.SequenceEqual(Convert.FromHexString(expectedHex)));
    }

    [Test]
    [TestCase(
        "0000000000000027d806bfddbebe11f7ee8a39fc7dc24498de85c8afca0000000000000000000000000000000001",
        "846b23860007d8d735a46364087f4fe90cfd2d129af7c593079f04186d2a71826c3ed4fefd7403eb452c4160d23c4d6c0d4f04d46333117a39aacc2c59e5384c5d5455bb6887e7979454948962afcb41e0f928c64a75001a71883f5ed7d02d81",
        "afc199da35e82d41a92b2f06cc05377f394cdce127394a4c49bc18e90c90b4d93cb1c2c6c232d7b8fbc7573ec724c0d5"
    )]
    [TestCase(
        "0000000000000027d806bfddbebe11f7ee8a39fc7dc24498de85c8afca00000000000001f4000000000000000001",
        "a4a2e5b38f761fe45084fb2ae14878873d5a3b99133aa86db36abc3318e7cd1ef12974420ffed2fa3cda382b8a51e6f304104887f2393c45cca95d9e0f9ddf6e18c054f2c4c755a03e601dff74a4bcc55210e13c134e3cf983c20515aafb5a71",
        "90dcd8a9b9b86b9df077e3fda45a92647422b00d70eb37372200703ef20e2d8a9eba57e9e09ae77d6c54ff8ded3b92b3"
    )]
    public void Can_verify_validator_registration_signature(string msgHex, string sigHex, string pkHex)
    {
        Assert.That(ShutterCrypto.CheckValidatorRegistrySignature(
            Convert.FromHexString(pkHex),
            Convert.FromHexString(sigHex),
            Convert.FromHexString(msgHex)
        ));
    }

    [Test]
    [TestCase(
        "02F8768227D882027384B2D05E0084B2D05E0E830186A094C34B4858CF5F89916559FAB2F864384D74B2F99687038D7EA4C6800080C001A07098F6E9A7A28BD3D78221658E9668C17413DF03920F261193A3355CA71E8BA1A02024DE99EAE02A59FDFBC549316320349C2CF9D968F2E69E5C35609194F671A6"
    )]
    public void Can_decode_transaction(string encodedTxHex)
    {
        Transaction tx = Rlp.Decode<Transaction>(Convert.FromHexString(encodedTxHex));
        Console.WriteLine(tx.ToShortString());
        Assert.That(false);
    }

    [Test]
    [TestCase(
        7649174914161947266ul,
        16729082666370017565ul,
        8333205535599204084ul,
        17223499624376311426ul,
        "0x932552E9df00550E4c59fA4C233B440743e85974",
        "a9358a3e475e373d4749b9bce38df386e90b5b84742d77881448a6ce0db07e3077f8652d0133488962b7543b642c1025066904fb5c4278b91be6892b86c314c400",
        new string[] { "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f202122232425262728292a2b2c2d2e2f3031323334" }
    )]
    [TestCase(
        60ul,
        1ul,
        10457208ul,
        619ul,
        "0xcb770a9b31ac28b0c90d0357f8df7c1c1cd660be",
        "C3A9E42322917542E56FCEB963612161ECE4751E5BB4232CE030E6C1FDD8AA9B01551565037C355874858CC595FCE595368F071572F7CE6F4BDD9DEA3A7514C800",
        new string[] { "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000009F9078" }
    )]
    public void Can_verify_decryption_key_signatures(ulong instanceId, ulong eon, ulong slot, ulong txPointer, string keyperAddress, string sigHex, string[] identityPreimagesHex)
    {
        List<byte[]> identityPreimages = identityPreimagesHex.Select(Convert.FromHexString).ToList();
        Assert.That(ShutterCrypto.CheckSlotDecryptionIdentitiesSignature(instanceId, eon, slot, txPointer, identityPreimages, Convert.FromHexString(sigHex), new(keyperAddress)));
    }
}
