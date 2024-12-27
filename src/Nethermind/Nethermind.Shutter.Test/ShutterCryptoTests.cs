// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Int256;
using NUnit.Framework;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Core.Extensions;

namespace Nethermind.Shutter.Test;

using G1 = Bls.P1;
using G2 = Bls.P2;
using GT = Bls.PT;
using EncryptedMessage = ShutterCrypto.EncryptedMessage;

[TestFixture]
class ShutterCryptoTests
{
    [Test]
    public void Pairing_holds()
    {
        UInt256 sk = 123456789;
        UInt256 r = 4444444444;
        G1 identity = G1.Generator().Mult(3261443);
        G2 eonKey = G2.Generator().Mult(sk.ToLittleEndian());
        G1 key = identity.Dup().Mult(sk.ToLittleEndian());

        GT p1 = new(key, G2.Generator().Mult(r.ToLittleEndian()));
        Span<byte> h1 = ShutterCrypto.Hash2(p1);
        GT p2 = new(identity, eonKey);
        ShutterCrypto.GTExp(ref p2, r);
        Span<byte> h2 = ShutterCrypto.Hash2(p2);

        Assert.That(h1.ToArray(), Is.EqualTo(h2.ToArray()));
    }

    [Test]
    [TestCase("f869820243849502f900825208943834a349678ef446bae07e2aeffc01054184af008203e880824fd4a0510c063afbe5b8b8875b680e96a1778c99c765cc0df263f10f8d9707cfa0f114a02590b2ce6dbce6532da17c52a2a7f2eb6155f23404128fca5fb72dc852ce64c6")]
    [TestCase("08825208943834a349678ef446bae07e2aeffc01054184af008203e880824fd4a02356f869820246849502f900825208943834a349678ef446bae07e2aeffc01054184af008203e880824fd4a02356b138904ed89a72a1fa913aa651c3b4144a5b47aa0cbf6a6cf9956d896bc0a0825208943834a349678ef446bae07e2aeffc01054184af008203e880824fd4a023560825208943834a349678ef446bae07e2aeffc01054184af008203e880824fd4a0235607e1364d24a98ac1cdb3f0af8c5c0cf164528df11dd766aa368d4136651ceb55e")]
    public void Can_encrypt_then_decrypt(string msgHex)
    {
        byte[] msg = Convert.FromHexString(msgHex);
        UInt256 sk = 123456789;
        G1 identity = G1.Generator().Mult(3261443);
        G2 eonKey = G2.Generator().Mult(sk.ToLittleEndian());
        Span<byte> sigma = new([0x12, 0x15, 0xaa, 0xbb, 0x33, 0xfd, 0x66, 0x55, 0x15, 0xaa, 0xbb, 0x33, 0xfd, 0x66, 0x55, 0x15, 0xaa, 0xbb, 0x33, 0xfd, 0x66, 0x55, 0x15, 0xaa, 0xbb, 0x33, 0xfd, 0x66, 0x55, 0x22, 0x88, 0x45]);

        TestContext.Out.WriteLine("eon key for " + sk + ": " + Convert.ToHexString(eonKey.Compress()));

        EncryptedMessage encryptedMessage = ShutterCrypto.Encrypt(msg, identity, eonKey, sigma);
        G1 key = identity.Dup().Mult(sk.ToLittleEndian());

        ShutterCrypto.RecoverSigma(out Span<byte> recoveredSigma, encryptedMessage, key.ToAffine());
        Assert.That(recoveredSigma.ToArray(), Is.EqualTo(sigma.ToArray()));

        Span<byte> decryptedMessage = stackalloc byte[ShutterCrypto.GetDecryptedDataLength(encryptedMessage)];
        ShutterCrypto.Decrypt(ref decryptedMessage, encryptedMessage, key);
        Assert.That(msg.SequenceEqual(decryptedMessage.ToArray()));

        EncryptedMessage decoded = ShutterCrypto.DecodeEncryptedMessage(ShutterCrypto.EncodeEncryptedMessage(encryptedMessage));
        Assert.That(encryptedMessage.C1.IsEqual(decoded.C1));
        Assert.That(encryptedMessage.C2.ToArray(), Is.EqualTo(decoded.C2.ToArray()));
        Assert.That(encryptedMessage.C3.ToArray(), Is.EqualTo(decoded.C3.ToArray()));
    }

    [Test]
    [TestCase(
        true,
        "9555a85e8f6f91b2c986c36e4047ed0613c5cd7ae5d9651d0d465a3d37a1a15dcf3102762a913e34bc5769c8c4a7fbd1",
        "ac7ac24084236ddd6f4bee47e4a10086ff02345cce206e12befcc328425704b179ad21b50a69434a6c851286959382291513f6a856da9166cc0cd8c03a09f0dfa34876a88e7c664aeee6edfd5159b9f31e79f8ec9db7797d57c5086b1ff695e1",
        "6baafa8f22cf1f3d32e8b1a13d6859eb92085c346fbed66f8def1dd1659ee555a925487f1fc87750627e9a1659b31adcac0157b3"
    )]
    [TestCase(
        false,
        "a7b225dad05e856fb58bd87bd805ed67466f731421de8fa6127ffd16964c2ac06c4dc40d27f71d4cb81edf9f1de42ff8",
        "87c0cbe2e20645dcf0d8805e7afae882f6d483765e7616f6e7454c015e0f34dcc1b4c9ed05169b053dbe12f2223a423606e51afdf59e3536f5bbc49555a67ed2d7fd7be3a893ba15e80f20415a51156a4844066583e98ad17c273ae921f28bdf",
        "c6af2324331998d25b488fafe9e7e25bc3a2e3d45b75e47dd8a4da5435db152c728403a7e2911eb21daeeee0c1342710c3e2d5b2"
    )]
    public void Can_check_decryption_keys(bool expected, string dkHex, string eonKeyHex, string identityPreimageHex)
    {
        G1 dk = new(Convert.FromHexString(dkHex));
        G2 eonKey = new(Convert.FromHexString(eonKeyHex));
        byte[] identityPreimage = Convert.FromHexString(identityPreimageHex);
        G1 identity = new();
        ShutterCrypto.ComputeIdentity(identity, identityPreimage);

        Assert.That(ShutterCrypto.CheckDecryptionKey(dk.ToAffine(), eonKey.ToAffine(), identity.ToAffine()), Is.EqualTo(expected));
    }

    [Test]
    // encryption 4
    [TestCase(
        "a24b9d6554912ef6874486ad8c42d3c0b0817997059d4c763c512bed3ccfd7fc",
        "87c1c4c78a302e3ba808ffa76bd555c1aa9fa2846fc24ff2424a41586a3d8ab2a60003cb00e1cea6858eb0fed14d9e8c91741948",
        "ac7ac24084236ddd6f4bee47e4a10086ff02345cce206e12befcc328425704b179ad21b50a69434a6c851286959382291513f6a856da9166cc0cd8c03a09f0dfa34876a88e7c664aeee6edfd5159b9f31e79f8ec9db7797d57c5086b1ff695e1",
        "184172a7457952a02255adc9b723be43e29f50c3e074b118f185434153698835",
        "038cad884b3457b04e2c1796722b7ed6e1f1da6a8290e0a013564bf139e167b11a114f70cb84e002292e517419a67265c10cf1d79a1e79bd8c2cc4fc56a408b2d54149e25ac53573f2c0006286271f70c456f0e10d4c5243b2004b529c03d8e3533c0a93a4510f6781ea682913b5b522ff47b1d8b3c5bd815409595e1d324cdcacda0e738d1a712698ae475549c684ff5f4289eca61d133764f1919b4d4bdd9081a19992bff0f40e056bae02dbd87cc71e482bc2efac89766c401c00fa1f708ae2"
    )]
    // encryption 9
    [TestCase(
        "41206d657373616765",
        "4609799139506408307ff4cd8933dc35d52e5e9d5c72b58b20ecb9cce93457b9e9f15dbb9683503def21bc5a8502978692764974",
        "ac7ac24084236ddd6f4bee47e4a10086ff02345cce206e12befcc328425704b179ad21b50a69434a6c851286959382291513f6a856da9166cc0cd8c03a09f0dfa34876a88e7c664aeee6edfd5159b9f31e79f8ec9db7797d57c5086b1ff695e1",
        "9bfb46b6ae9c1054a51eb8c639ce204e452ddf8eb75661c5ca34ca6861458584",
        "03a33b390fdb98612a13dbfe05490aa6027e350ca3b204e57e0cef8fccd7ef57928850efc82d061702b889d3dbc02bd8f415dcb15c802d86733f217d8509f9ad6c666e9caac3cc45c63efa7b9dadeaf426254e4c41a69f7fccecf8f4820aecd0c419fdae1dd00353b498a510c25ebcaa902fc03b84254dc386bf9e51a8e77b47597a9063e1fe10ba563a13cd88ff4e66ee8135c029475de5ff559fdcc1aca86e3c"
    )]
    // encryption 2
    [TestCase(
        "c1",
        "1e3432a664e7674f99c4bebda3de85af745db21d20f53a8d7adefc9ce46c39721b73797a3dfd72fd7c45ac01a15e011acc9e187b",
        "ac7ac24084236ddd6f4bee47e4a10086ff02345cce206e12befcc328425704b179ad21b50a69434a6c851286959382291513f6a856da9166cc0cd8c03a09f0dfa34876a88e7c664aeee6edfd5159b9f31e79f8ec9db7797d57c5086b1ff695e1",
        "0608fb237111797214a33cd1ab072f0a576fc0b3016b3de669a42642c9cae165",
        "03b902dcba95b9d0fd5b9000fbb79a840516ec62a8082beed4fcf725035e32282d7efe4209c50a5a578eef1be47cc30cc30b0edfb3b61e24af605d70616ac0bed3f79baca921492e88f72ccc8091d053f6c392bfe3c8e202e0a025e50aa4b404c9decdea2289f11fe0a377286cdaa460752096318210010b75dd6ee47bca1b44e121d4be9fe788c1ed2cf9f885c8bd6939baa64fa0add349ff864a61ae4a18586c"
    )]
    public void Can_encrypt_data(string msgHex, string identityPreimageHex, string eonKeyHex, string sigmaHex, string expectedHex)
    {
        byte[] msg = Convert.FromHexString(msgHex);
        byte[] expected = Convert.FromHexString(expectedHex);

        G1 identity = new();
        ShutterCrypto.ComputeIdentity(identity, Convert.FromHexString(identityPreimageHex));
        G2 eonKey = new(Convert.FromHexString(eonKeyHex));
        Span<byte> sigma = Convert.FromHexString(sigmaHex).AsSpan();

        EncryptedMessage c = ShutterCrypto.Encrypt(msg, identity, eonKey, sigma);

        Span<byte> encoded = ShutterCrypto.EncodeEncryptedMessage(c);
        TestContext.Out.WriteLine("encrypted msg: " + Convert.ToHexString(encoded));
        Assert.That(encoded.ToArray(), Is.EqualTo(expected));
    }

    [Test]
    // cryptotests decryption 4
    [TestCase(
        "038cad884b3457b04e2c1796722b7ed6e1f1da6a8290e0a013564bf139e167b11a114f70cb84e002292e517419a67265c10cf1d79a1e79bd8c2cc4fc56a408b2d54149e25ac53573f2c0006286271f70c456f0e10d4c5243b2004b529c03d8e3533c0a93a4510f6781ea682913b5b522ff47b1d8b3c5bd815409595e1d324cdcacda0e738d1a712698ae475549c684ff5f4289eca61d133764f1919b4d4bdd9081a19992bff0f40e056bae02dbd87cc71e482bc2efac89766c401c00fa1f708ae2",
        "95e84d8032618db55029bc7b60dbd0b0dbdae5da3fb89e38aa53bf5b84ea1b77cc6ba3ebd9eab5da54e7971fa79c816d",
        "a24b9d6554912ef6874486ad8c42d3c0b0817997059d4c763c512bed3ccfd7fc"
    )]
    // decryption 6
    [TestCase(
        "03882ff5ca64d6afcdde711841b969377f4d8fe0f9f5cebe519a4d064fda6842d8ab01fd858cb3714083169a78c7cb8dbd047acf11e2ed461a4af515714ca0318ec445cb3b400778320fa936c19455e6a4d205a6e9c1dafea0628918e5355b07588ed9b53cf49f8678d3099d0cb335c2b3b673effc30605faabf6af6f5751592e70a5b4e7332b18a08add17e36aac5db2c8c3ed19e3a96021ff4f73bb7ca0662b7b575c2775335eb56e20ad9216b7376df2a0ff80d12a9892dbbc969e5df8acfd89f15648700d1f1e14a6c57d376a6ddc661dcd047a8803604c06bacb24162a08d5a3587f35c307c44be7465b9b982cf1c40f2f90b11d8f4623013a2d516949cf1d820b91f6a80f7bcc1f0a04dd68f3dbd992a4866e33e17fd09b44b1b4e36f891754b23d2928f5106df445b065527235a8a6ebdb811a1f7325ba38ee2db602041bea9777065ec36a6f7ba0fcb897bf29cccf383883e7e571b772ca62199e4d56fc324219c55ae7ae92e5b2d84c03af19f78fae1a400f88a3adbce5ddc77ae2963a4746e629a5e5d5eb082488d382a3c1a16727cdb1f3849e4490ff754a88fd0260ce4d476e2c96c67b207bc83657a4f90560a314573a5e369090df1766d96f5cd",
        "90adf61b0bbca0a477f12e6429c53735966ae10c77844e8989355cec7c2ef8e1cbe0f162463014034688bcebf98f5e9e",
        "1e366ff59c0e891e094e1a10a2bf2c8a8d7091bb0a2c725d324984a9131304b16a7fba17d65ec729c010c5741711747a3ee5cc59a0730bd2eac0b538a8cfa9430beebe984758067e9e555283dcbeb1495444a36b0d52384a08f29b638ffd30006ed13130ada213e693defe568a3f2b9d251ce996fcc4dd1d07e83a23b4251358d80b81ccf1a5936b747fbe6d4f2fca241d1f149267d592717b4302aa06ed48c4f098f0ca782e229b619dfe390490b64d38712fbaae8edf044d0f78f696bf7c522dec976d5f219f7254ad5ac0f8346481064710f02c4db0f1aa2164afd0b6153de841070444e013876e032adfa3e3b0dab8a337f6414701b48fdafcc527535622688a06b490851463f60ae0385e218858d1f3a3a9418eeef8f931c50ec13c23bf8543889dfa22d891bee0d6efc2cce322517bd6ba429b5647bed0254cf42eed"
    )]
    public void Can_decrypt_data(string cipherTextHex, string decryptionKeyHex, string expectedHex)
    {
        EncryptedMessage c = ShutterCrypto.DecodeEncryptedMessage(Convert.FromHexString(cipherTextHex));
        G1 decryptionKey = new(Convert.FromHexString(decryptionKeyHex));

        // recover sigma
        GT p = new(decryptionKey, c.C1);
        Span<byte> sigma = ShutterCrypto.Hash2(p); // key
        sigma.Xor(c.C2);

        int len = ShutterCrypto.GetDecryptedDataLength(c);
        Span<byte> decryptedMessage = stackalloc byte[len];
        ShutterCrypto.Decrypt(ref decryptedMessage, c, decryptionKey);
        TestContext.Out.WriteLine("decrypted msg: " + Convert.ToHexString(decryptedMessage));

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
        BlsSigner.AggregatedPublicKey pk = new();
        pk.Decode(Convert.FromHexString(pkHex));

        Assert.That(ShutterCrypto.CheckValidatorRegistrySignatures(pk, Convert.FromHexString(sigHex), Convert.FromHexString(msgHex)));
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
        IEnumerable<ReadOnlyMemory<byte>> identityPreimages = identityPreimagesHex.Select(Convert.FromHexString).Select(static b => (ReadOnlyMemory<byte>)b);
        Assert.That(ShutterCrypto.CheckSlotDecryptionIdentitiesSignature(instanceId, eon, slot, txPointer, identityPreimages, Convert.FromHexString(sigHex), new(keyperAddress)));
    }
}
