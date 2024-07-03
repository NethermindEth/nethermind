// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain.BlockHashInState;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Verkle;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Tree.Serializers;
using Nethermind.Verkle.Tree.TreeStore;

namespace Nethermind.Verkle.Tree.Test;

public class VerkleProofTest
{
    [Test]
    public void TestProofVerifyTwoLeaves()
    {
        byte[][] keys =
        {
            VerkleTestUtils.EmptyArray,
            VerkleTestUtils.ArrayAll0Last1,
            VerkleTestUtils.MaxValue,
        };
        VerkleTree tree = VerkleTestUtils.CreateVerkleTreeWithKeysAndValues(keys, keys);
        VerkleProofSerialized proof = tree.CreateVerkleProof(keys, out Banderwagon root);

        TestProofSerialization(proof);

        bool verified = VerkleTree.VerifyVerkleProof(proof, new List<byte[]>(keys), new List<byte[]?>(keys), root, out _);
        Assert.That(verified, Is.True);
    }

    [Test]
    public void TestVerkleProof()
    {
        List<byte[]> keys = new()
        {
            VerkleTestUtils.KeyVersion.BytesToArray(),
            VerkleTestUtils.KeyBalance.BytesToArray(),
            VerkleTestUtils.KeyNonce.BytesToArray(),
            VerkleTestUtils.KeyCodeCommitment.BytesToArray(),
            VerkleTestUtils.KeyCodeSize.BytesToArray()
        };

        List<byte[]> values = new()
        {
            VerkleTestUtils.EmptyArray,
            VerkleTestUtils.EmptyArray,
            VerkleTestUtils.EmptyArray,
            VerkleTestUtils.ValueEmptyCodeHashValue,
            VerkleTestUtils.EmptyArray
        };
        VerkleTree tree = VerkleTestUtils.CreateVerkleTreeWithKeysAndValues(keys.ToArray(), values.ToArray());

        VerkleProofSerialized proof = tree.CreateVerkleProof(keys.ToArray(), out Banderwagon root);
        TestProofSerialization(proof);

        bool verified = VerkleTree.VerifyVerkleProof(proof, keys, values, root, out _);
        Assert.That(verified, Is.True);
    }

    [Test]
    public void BasicProofTrue()
    {
        VerkleTree tree = VerkleTestUtils.GetVerkleTreeForTest<VerkleSyncCache>(DbMode.MemDb);

        byte[][] keys =
        {
            new byte[]{0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0},
            new byte[]{1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0},
            new byte[]{2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0},
            new byte[]{3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0}
        };

        tree.Insert((Hash256)keys[0], keys[0]);
        tree.Insert((Hash256)keys[1], keys[1]);
        tree.Insert((Hash256)keys[2], keys[2]);
        tree.Insert((Hash256)keys[3], keys[3]);
        tree.Commit();
        tree.CommitTree(0);

        VerkleProofSerialized proof = tree.CreateVerkleProof(keys, out Banderwagon root);
        TestProofSerialization(proof);

        const string expectedProof = "00000000040000000a0a0a0a0800000056778fe0bcf12a14820d4c054d85cfcae4bdb7017107b6769cecd42629a3825e38f30e21c" +
                                     "79747190371df99e88b886638be445d44f8f9b56ca7c062ea3299446c650ce85c8b5d3cb5ccef8be82858aa2fa9c2cad512086db5" +
                                     "21bd2823e3fc38107802129c490edadbab32ec891ee6310e4f4f00e0056ce3bb0ffc6840a27577556a6fa8933ab25ddb0fb102fca" +
                                     "932ac58a5f539f83b9bfb6e21ea742aa5ad7403f36f9c0821d7014e7a7b917c1d3b72acf724906a30a8fefb09889c3e4cfbc528a4" +
                                     "0fd3331b653dea7be3fe55bc1a0451e0dbad672ec0236cac46e5b23d13d5562743b585beaf3dc1987c9bdf5701af9c4784a392549" +
                                     "9bd6318b63ccec4b35f914211ff8aae6b4cf60eff5923a10ef205901fe311348cd3cfd00f236fa1025621e9998a57d92290af6e64" +
                                     "c0313f963f469b1aa62ccdf5358db751e99c3492442bedb541990bab710eef8ba2c1319e830b18528fd7e350abc296d9d084139f0" +
                                     "9761e10712063beb75f11888b1465ec1013308ea54c05ecda8cf4600668fb1013b2e1b1b84b130b73aa2dd2974aae4977319d7713" +
                                     "4400a1f06ee1010fdf6349553be941c98a8f281ab3b9348c2bed9cbbfddde4e3e278ed30f4ce10434deec667fbbedf8b16b6e4922" +
                                     "dfc9d6084338bb431b8d1bb40ef861007a992057d28f7117eb095d5992df28d9a3d755317ea208be967461f60538e6670d7b305a9" +
                                     "62755e668c01a28c5521143e99aac7174b33af6897bca30c28593521b9c261bb8d782b230bdcb97a71e2d55711a617905cad3ecec" +
                                     "f78e60a9c309a62eb48bb0223f8135b5209ff1b54784b3c5acfa93c0606dbeca6f15262174eced2cd51e45b50f83b0cdfaca12af8" +
                                     "b31a8381df4a6c6632c5cf0018b3048b84882ec381f238c1e73a1b54f6007c4c174136a8026a7a4bd32b02bda3a0ac7606b8bd43b" +
                                     "dfc5b21406139e8119b8ed8036745c124e2971628744441e110282344ab67f55163a117602b53d2dcc752b61f0cd7a3422b76d0a2" +
                                     "08b03e0f52288919ade628ada07031e271c10742c665e3af0e74d467b8bd5b82b9a832ad95f0e202abdd7cfccf6968e7439db22e6" +
                                     "261fa572bc57d4a0a45963efbf23b08855cbfc43ecf1e9239536a430995ea4f59d800e4ee5a6fd9dd8fbab1f4e5ccd5ce71a77096" +
                                     "fc7cad3d74d98d2cb49faacef38d22a92985f73550ebf8add9377150925b974d9c8d86002ef8346eb33f7bae72c36ec55e3919276" +
                                     "58bcbc9b46ba45572fce51e814bb38123c5ac4a12104781187e7f24472d68d3c79e6965dd37638613731eda814402d7547dce0447" +
                                     "34adc30eb7a36216a37f2e2c857a128c54a7a12dc0b819c5331a08f9a95e4ce97fa4506fd4e991bdb13d5d85d03c46e21c49c559d" +
                                     "e266e50fc8d37badd993ff8fc1c996ed850d712f4e9689e9980e051988403d1bc212018ad096c2c93550da9d09e45f0b06fe1c21e" +
                                     "23e89820a825a2aea0028ec92d1dd143e2b992c7bf3ed60fe1a15900146997e70180a9bb2191c604b90edad3bea69e4e2874c1e00" +
                                     "10390652972a16765ace6256c751222ff1681610872fca93542df1b62069e4c7f330273bb2243b7df5469c5f6b88f7162dec0eb04" +
                                     "ca8e348ce186601bd8c3a20d55f379c0635b7caec6f1c88baee2f9056d4cdfccd7314d131c534cdae825d8bf34ae79dd9620d54e9" +
                                     "ac9c9dbd377aa4d6676eaddd47e64b32275a786e998b1e9597efc7447443ae6862585f9f2b073bbbe079d1e9757777fc5e2d5f04f" +
                                     "e39eb6102136bc6125d0b9042a45b2e982686d034e8404b9b12b6a2016c31b6c4c490a4890e88f797057a166d82239fb588202c84" +
                                     "50a858999ef45cba91cbbb2fc3aa85f4874a519813b771ad0e91f16665f9aff6b79f23d5aecff065e4362190459fc2551340ac112" +
                                     "0001c921a36dd8c2d441a93f4fa4fd6a71d54f0f862340cf9e401b";

        Assert.That(proof.Encode().ToHexString().SequenceEqual(expectedProof), Is.True);
        bool verified = VerkleTree.VerifyVerkleProof(proof, new List<byte[]>(keys), new List<byte[]?>(keys), root, out _);
        Assert.That(verified, Is.True);
    }

    [Test]
    public void BasicProofTrueSameStem()
    {
        VerkleTree tree = VerkleTestUtils.GetVerkleTreeForTest<VerkleSyncCache>(DbMode.MemDb);

        byte[][] keys =
        {
            new byte[]{0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0},
            new byte[]{0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1},
            new byte[]{0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2},
            new byte[]{0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3}
        };

        tree.Insert((Hash256)keys[0], keys[0]);
        tree.Insert((Hash256)keys[1], keys[1]);
        tree.Insert((Hash256)keys[2], keys[2]);
        tree.Insert((Hash256)keys[3], keys[3]);
        tree.Commit();
        tree.CommitTree(0);

        VerkleProofSerialized proof = tree.CreateVerkleProof(keys, out Banderwagon root);
        TestProofSerialization(proof);

        const string expectedProof = "00000000010000000a020000000b2cd97f2703f0e0030f8356c66ef9cda8587109aab48ebdf02fd49ceefa716d1731296" +
                                     "d27f24eddf8e4576bcf69395373a282be54e0d16966c5ac77f3423b9f4605765280e8099881f4cc64ead200148f7b826c" +
                                     "4d68e4470ed36ac72c05f61a94dbaea0c5e85840a92df0215f698dd6d8a6f1ccd8c5f2620ff6f9b8cc3c7937800b8e502" +
                                     "04fc14b10865c09db726f89570191d3fd9e36c455c6bbef169be0168740f0c3257ae933f5b00f4cfbaee7f5c4d9fac42b" +
                                     "bfff1bbb9c77bb8722d611a0a809e64f46d9345b060c6a75be61bf2fe619571df18cbcdef9cf3d44ff361d4039f1757d4" +
                                     "ed0c5ac848755075a43a2706b85eaa2c465801325498b27c88149335fb00c0ec6666b6cadd47107b3e2cfba70a40b16f6" +
                                     "a81ba699b715eabd06444daa191a03f7f892c7518503cbb97d92c452fccbda965b82d00f7bacab971930cff350456796b" +
                                     "39ec86c58020de7055557d8ac9e04b876d453aa56a4373d8a27d893a9d966e58f2e55625235be98cd672993de6be07469" +
                                     "6deb1219481317bf04f4ef246b6accbe52f3311a1f3f1cd211a2a6a412676a466378f8a6f97cf8c32d10cdbd3a22c481d" +
                                     "8579d425761944e21a932baee4c179164a3a7fe57785a453f41806ca9782a18935c374d9ce745f3af1ae6705b19ac839f" +
                                     "a824aa257d80157131daed70f4f662fde1fdc31eb633fd92ae6c43dfc5a4ccdb888db2ff85cf706135cd41cf345a1615d" +
                                     "40947a8326f0854793f5ae23c380b7b5f779712eb3b473f06fe2a32c50efc2bfdfb364d04003cffe4a51ca1566757b1fd" +
                                     "406e2035c7a26ebecd8e9598358b372147f344d2c654fa273cdce8f8896fb60ab4c5f26a2e1a3f5c76ffe21ee22581e4f" +
                                     "9a3ec8ebc0b04b9ce378057392b4934d4400b9228901ab88bb6bd4b7dfa3ec7561fd0783b0570fc6b012e8abf574392cc" +
                                     "43466820e932230af0593fa7c7922c1566fa456d666bfa70a659feb72b6f4b34fc919b4ef56bdfc7c27eff2154970c8c3" +
                                     "ac9ed3dc2bcf0e1ad6fa31e56ebf3064b19f564c93d80eff91f39fefc5c6df668af064eb1da40b0f8ea86070a46d12832" +
                                     "2cd2b98f572a6ab22f8f4e0de9969d0675d2cb6c47c3ac4fa7aaf01bbe5171323077b3a449bd9779f8db5dd8c654d8f32" +
                                     "b4dd4e4ea1b924ba9921559bcce0be524f9ff6f496bff11f732b037e457dcef0bf7c237213393d23c98d605fd3054fad5" +
                                     "4430d153d7f9177547eee134b51f7de813758492f1faae0e18aa60b9555fc4d91d24394117c05f5d3c3ace94815d585b7" +
                                     "273ed8cf60aaa280309452407662c9b5845705e032c10b473c902e6510ef77a8419dd5741ef4dc995ecc178beb6ac3cdf" +
                                     "87b933c65a46c937a94a06b0b00bad3c296d8242e3c0fa4b047e9a6a56bd8fa5d42e48855fcd09eb875df3a42e90cc809" +
                                     "7c6a79a4fb2c7846ebc4c09665c53cec05327c073a669d56cd67edbce49fc2f514770df7389f82dd99012aaf4ba4d584c" +
                                     "3a5f75af29408421ff7be8c604f0ac07b739fb91b363678b35452d8bd6bc821ce30077d3ccdf82eb72af1a48da30e10fe" +
                                     "2a90bed52bedba033bcd8c1034e61e4740c65d22853b04a3b8733987e93e1c77dffac49f50d09de4ecaf835f94f3877e6" +
                                     "480f9d8844fc5fa98f2ec370e18b24da702840f21f71e24ca999bbce608ca5f507";

        Assert.That(proof.Encode().ToHexString().SequenceEqual(expectedProof), Is.True);

        bool verified = VerkleTree.VerifyVerkleProof(proof, new List<byte[]>(keys), new List<byte[]?>(keys), root, out _);
        Assert.That(verified, Is.True);
    }

    [Test]
    public void ProofOfAbsenceEdgeCase()
    {
        VerkleTree tree = VerkleTestUtils.GetVerkleTreeForTest<VerkleSyncCache>(DbMode.MemDb);

        byte[][] keys =
        {
            new byte[]
            {
                3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3
            },
        };

        VerkleProofSerialized proof = tree.CreateVerkleProof(keys, out Banderwagon root);
        TestProofSerialization(proof);

        const string expectedProof =
                            "000000000100000008000000000000000000000000000000000000000000000000000000000000000000000000010000000000" +
                            "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
                            "000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
                            "000000000000000000000000000000000000000001000000000000000000000000000000000000000000000000000000000000" +
                            "000000000000000000000000000000000000000000000000000000000000000000010000000000000000000000000000000000" +
                            "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000100000000" +
                            "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
                            "000000000000000001000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
                            "000000000000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000" +
                            "000000000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000" +
                            "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001000000" +
                            "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
                            "000000000000000000010000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
                            "000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000" +
                            "000000000000000000000000000000000000000000000000000000000000000000000001000000000000000000000000000000" +
                            "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000010000" +
                            "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
                            "000000000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000" +
                            "000000000000000000000000000000000000000000000001000000000000000000000000000000000000000000000000000000" +
                            "000000000000000000000000000000000000000000000000000000000000000000000000010000000000000000000000000000" +
                            "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000100" +
                            "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
                            "0000000000000000000000";

        proof.Encode().ToHexString().Should().BeEquivalentTo(expectedProof);
        List<byte[]?> values = new() { null };
        bool verified = VerkleTree.VerifyVerkleProof(proof, new List<byte[]>(keys), values, root, out _);
        Assert.That(verified, Is.True);
    }

    [TestCase(1, 1)]
    public void BenchmarkProofCalculation(int iteration, int warmup)
    {
        IDbProvider db = VerkleDbFactory.InitDatabase(DbMode.MemDb, null);
        IVerkleTreeStore store = new VerkleTreeStore<VerkleSyncCache>(db, LimboLogs.Instance);
        VerkleTree tree = new VerkleTree(store, LimboLogs.Instance);
        byte[][] keys = new byte[1000][];
        for (int i = 0; i < 1000; i++)
        {
            keys[i] = TestItem.GetRandomKeccak().Bytes.ToArray();
            tree.Insert((Hash256)keys[i], Keccak.Zero.Bytes);
            tree.Commit();
        }
        tree.CommitTree(0);

        Stopwatch sw = new Stopwatch();

        for (int i = 0; i < warmup; i++)
        {
            tree.CreateVerkleProof(keys[..500], out _);
        }

        sw.Start();

        for (int i = 0; i < iteration; i++)
        {
            tree.CreateVerkleProof(keys[..500], out _);
        }

        sw.Stop();

        Console.WriteLine("Elapsed={0}", sw.Elapsed / iteration);
    }

    [Test]
    public void RandomProofCalculationAndVerification()
    {
        Random rand = new(0);
        IDbProvider db = VerkleDbFactory.InitDatabase(DbMode.MemDb, null);
        IVerkleTreeStore store = new VerkleTreeStore<VerkleSyncCache>(db, LimboLogs.Instance);
        VerkleTree tree = new(store, LimboLogs.Instance);
        byte[][] values = new byte[1000][];
        for (int i = 0; i < 1000; i++) values[i] = Keccak.EmptyTreeHash.Bytes.ToArray();

        SortedSet<byte[]> keysSet = new(Bytes.Comparer);
        while (keysSet.Count != 1000)
        {
            keysSet.Add(TestItem.GetRandomKeccak(rand).Bytes.ToArray());
        }

        byte[][] keys = keysSet.ToArray();

        for (int i = 0; i < 1000; i++)
        {
            tree.Insert((Hash256)keys[i], values[i]);
            tree.Commit();
        }
        tree.CommitTree(0);

        VerkleProofSerialized proof = tree.CreateVerkleProof(keys[..500], out Banderwagon root);
        //TestProofSerialization(proof);
        bool verified = VerkleTree.VerifyVerkleProof(proof, new(keys[..500]),
            new(values[..500]), root, out _);
        Assert.That(verified, Is.True);
    }

    private void TestProofSerialization(VerkleProofSerialized proof)
    {
        VerkleProofSerializer ser = new();
        var stream = new RlpStream(Rlp.LengthOfSequence(ser.GetLength(proof, RlpBehaviors.None)));
        ser.Encode(stream, proof);
        VerkleProofSerialized data = ser.Decode(new RlpStream(stream.Data!));
        data.ToString().Should().BeEquivalentTo(proof.ToString());
    }

    [Test]
    public void TestBlock257()
    {
        string payload =
            @"{
                ""parentHash"":""0x4ff50e1454f9a9f56871911ad5b785b7f9966cce3cb12eb0e989332ae2279213"",
                ""feeRecipient"":""0xf97e180c050e5ab072211ad2c213eb5aee4df134"",
                ""stateRoot"":""0x5bf12e46f1ce048f74229eb6fb4bbdb715eb615e0b79abf32a53712a9f643de7"",
                ""receiptsRoot"":""0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421"",
                ""logsBloom"":""0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"",
                ""prevRandao"":""0xb5f43407166c107f53f501a3409e3b74cca112cc60c2bdc169d4d1476461721d"",
                ""blockNumber"":""0x101"",
                ""gasLimit"":""0x1c9c380"",
                ""gasUsed"":""0x0"",
                ""timestamp"":""0x65c21634"",
                ""extraData"":""0xd983010c01846765746889676f312e32302e3133856c696e7578"",
                ""baseFeePerGas"":""0x7"",
                ""blockHash"":""0xbedc42451ae09174af2a141ca36bee23337f3cdf6036ca68358a3decc1454057"",
                ""transactions"":[],
                ""withdrawals"":[],
                ""executionWitness"":{
                    ""stateDiff"":[
                        {
                            ""stem"":""0x117b67dd491b9e11d9cde84ef3c02f11ddee9e18284969dc7d496d43c300e5"",
                            ""suffixDiffs"":[
                                {
                                    ""suffix"":0,
                                    ""currentValue"":null,
                                    ""newValue"":""0x4ff50e1454f9a9f56871911ad5b785b7f9966cce3cb12eb0e989332ae2279213""
                                }
                            ]
                        }
                    ],
                    ""verkleProof"":{
                        ""otherStems"":
                            [
                                ""0x1123356d04d4bd662ba38c44cbd79d4108521284d80327fa533e0baab1af9f""
                            ],
                        ""depthExtensionPresent"":""0x09"",
                        ""commitmentsByPath"":[
                            ""0x3514e85936b1e484f84fc93b58e38b9ed9b0d870002653085f9dd758b1b8c3f5""
                        ],
                        ""d"":""0x59a7b2bd71eff71d6d43abd61c1533246dce01a1b973c9287e2c9b6dea76c21a"",
                        ""ipaProof"":{
                            ""cl"":[
                                ""0x07c7bc1903217d5eb06e067cd535376c7fa737e717a06cd62686e9dcebbef0a2"",
                                ""0x11ca24317a6772c962252e1d1963450027c5024152aef68b148fb833e30c0a99"",
                                ""0x0c330e01ce8c356e2ad9ba15f3ac4d730e5f986bf0f227b28c55ccdbfb373674"",
                                ""0x380b8e7d511dcd37d9596c084d156eaff21eec9368745c1222b4bfc2ce231db8"",
                                ""0x27c74c37d499e3475f4ad8968417e72b068ba923ffde27e21d323c29fe30ce78"",
                                ""0x5d2ebbdb282ccc9aa7f878a8875b01072e7444a02ee7f37daefe7745dc95ad32"",
                                ""0x494d512c207264ec018ca26d137bb46d6e9c68c934ae40d8c3d55024877a68f2"",
                                ""0x1b73f07a94186b7c3221d46e2082049e79c3b90999c9fc61970d9f2fbd5fbf96""
                            ],
                            ""cr"":[
                                ""0x6bbdeac4b34f9c03ea3d7d194702f7da05b3067609a0b4f163280f39a20ae781"",
                                ""0x596e6fdf00232028cdc19ad3830920b4fb685993031288355b4858280f14071b"",
                                ""0x2b8a39633e7a69c80c436f1250175476ba5a5dff2432e246f5478d5ba68b0903"",
                                ""0x2b5a3ad002ddff1d649ef4f03ea053baac62f4cd038fd391aff527f73bdd568a"",
                                ""0x409f15bc99f774b2a0f5b2ea4a06c1d7779d24776d013bc93f99d264f558b35f"",
                                ""0x49903ee86b289a82033d7887aeecdefd60ed390fb470252ca0101619377ec1eb"",
                                ""0x254ad6cf40e599e207698c6fe438d51b221336f0a6ee3297b7a2113fc09b37b4"",
                                ""0x3bad9aabbf4dc90e786d1b69c9c87e63974a376067029ed5e3d7fa04b701fd96""
                            ],
                            ""finalEvaluation"":""0x1349fd2896502fe7778d871bf10c4b7820744255178b429c08512c64c6e96d2c""
                        }
                    }
                }
            }";
        Console.WriteLine(payload);
        IJsonSerializer serializer = new EthereumJsonSerializer();
        ExecutionPayload? result = serializer.Deserialize<ExecutionPayload>(payload);
        Console.WriteLine(result);

        VerkleWorldState state = new VerkleWorldState(result.ExecutionWitness,
            Banderwagon.FromBytes(
                Bytes.FromHexString("0x3d339c16f3b906126a2f00f7130ff3bca99a176f1c97185ddf1e783417986510")).Value,
            SimpleConsoleLogManager.Instance);

        BlockHashInStateHandler handler = new BlockHashInStateHandler();
        result.TryGetBlock(out var block);
        handler.AddParentBlockHashToState(block.Header, Prague.Instance, state, NullBlockTracer.Instance);
        state.Commit(Prague.Instance);
        state.CommitTree(257);
        var stateRoot = state.StateRoot;
        stateRoot.ToString().Should()
            .BeEquivalentTo("0x5f8ddd98ea9608577ba161510d6c2284361ba55cab91ee2e6407ee2fe5a54cff");
    }


    [Test]
    public void TestPayload()
    {
        string payload =
            "{\"parentHash\":\"0x8493ed97fd4314acb6ed519867b086dc698e25df37ebe8f2bc77313537710744\",\"feeRecipient\":\"0xf97e180c050e5ab072211ad2c213eb5aee4df134\",\"stateRoot\":\"0x5492e10becd82299146790c27bf5c0e5311120f9528b9a9b4c1113d923880ef6\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"prevRandao\":\"0x8493ed97fd4314acb6ed519867b086dc698e25df37ebe8f2bc77313537710744\",\"blockNumber\":\"0x1\",\"gasLimit\":\"0x17dd79d\",\"gasUsed\":\"0x0\",\"timestamp\":\"0x65c20a34\",\"extraData\":\"0xd983010c01846765746889676f312e32302e3133856c696e7578\",\"baseFeePerGas\":\"0x342770c0\",\"blockHash\":\"0x7075b1f6516e064b36842610ddfe77eec27d1cb0b0c513fb43c2039c80fff796\",\"transactions\":[],\"withdrawals\":[],\"executionWitness\":{\"stateDiff\":[{\"stem\":\"0x97f2911f5efe08b74c28727d004e36d260225e73525fe2a300c8f58c7ffd76\",\"suffixDiffs\":[{\"suffix\":64,\"currentValue\":null,\"newValue\":\"0x8493ed97fd4314acb6ed519867b086dc698e25df37ebe8f2bc77313537710744\"}]}],\"verkleProof\":{\"otherStems\":[],\"depthExtensionPresent\":\"0x10\",\"commitmentsByPath\":[\"0x59ea4fd33c082589a35344c05b4171035ee524ff9e97c80ca0b4af039ac2e923\"],\"d\":\"0x39b1bb1c647f4cfa7ce0986c7d86ab2fb28e43ab47f07aaf687b2ec388810f98\",\"ipaProof\":{\"cl\":[\"0x07c5760a8988b794efcdea332a0a789dc4b3e4015ec769326b3bd04c5c21de70\",\"0x2ff4cf18e5c81dc3fdce9f8bb519c3a21f7e916bc1a99423ff1dcae29f52aa41\",\"0x3561126282d416a5decfc672b7f218f16edf709ce345828727ba8762bd53789a\",\"0x61d93267e569b8ee463801aa4ee7d742c3cf481573e977d1193ea2e23fa957ae\",\"0x0d27d9f98a34e477fc7526708f673564b7b99884501dbe6bdcafae8f6ae3630b\",\"0x488bbf3400c9bf2035a608b4a17cd653f2ec27cc7332a05eee338fa5830580be\",\"0x6537e4466d33ebbf1208760869aa149616f903e8c2f9fb8be8866861b1fa4e0e\",\"0x22209d41b8b08d2fd7e08cac6b7360ff32bb80bf5943733ebdab9f6ec3eb84de\"],\"cr\":[\"0x00b4f1dd3a79c1f88cb3473f05af31849b12b75b733ce1dbf023e0b77095d867\",\"0x3748f43f1e4b73f2646dfc9ab34914b2f1e0e6302215ec985f20fdba1b9cc15e\",\"0x03deb220bfa4063f300fdf79244fdb1b06ce363d53dd9463961e82504d054b7a\",\"0x62a48c7da07d29a5e8b42405cb57912e25af6c5fcf351e2dc0e54446f169d9b1\",\"0x3b51c0535934231e2c2fc047908657003c8aacb866c974e970ba9fc592c3ff42\",\"0x5a8610a73bfce9fe2bfb9c789ed273b81ce96123fe40379cd6167edfd2d2325d\",\"0x336ee042446643de31c5525f3146ac81d956075ef8a6966a95845c8a499478e8\",\"0x18a60d1fa8387f799f31ae7a1c9f57b3edd2ef62925a4fffe4a00ecfbd9401d1\"],\"finalEvaluation\":\"0x07bac830bd5640b598863dee03cfbe5a27c5f23e6d1d71c5b277f0e4b09afa36\"}}}}";

        IJsonSerializer serializer = new EthereumJsonSerializer();
        ExecutionPayload? result = serializer.Deserialize<ExecutionPayload>(payload);
        Console.WriteLine(result);
        Console.WriteLine(serializer.Serialize(result));
    }

    [Test]
    public void TestPayloadV2()
    {
        string payload =
            "{\"parentHash\":\"0x88da084b1706884ea0a7362178cce5b5e94492ebb80133b650062881cde70f93\",\"feeRecipient\":\"0xf97e180c050e5ab072211ad2c213eb5aee4df134\",\"stateRoot\":\"0x0fc6ab295b6495bbcc251008298fb10b766c390a996e3c1d2684544836b482c8\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"prevRandao\":\"0xe2f8ca204fe2ea776f134fd7a5977bf1d513dcece2843d947630c39cc39383ed\",\"blockNumber\":\"0x82\",\"gasLimit\":\"0x1b113b2\",\"gasUsed\":\"0x0\",\"timestamp\":\"0x65c21040\",\"extraData\":\"0xd983010c01846765746889676f312e32302e3133856c696e7578\",\"baseFeePerGas\":\"0x21\",\"blockHash\":\"0x158ab203a4a0fae08af42e65c4258e4f669f6c9e797f021d2d0d6341b3120efb\",\"transactions\":[],\"withdrawals\":[],\"executionWitness\":{\"stateDiff\":[{\"stem\":\"0x5e34e931327dce57bec2e61096273107d9cca52ad079988676712590fc205c\",\"suffixDiffs\":[{\"suffix\":129,\"currentValue\":null,\"newValue\":\"0x88da084b1706884ea0a7362178cce5b5e94492ebb80133b650062881cde70f93\"}]}],\"verkleProof\":{\"otherStems\":[],\"depthExtensionPresent\":\"0x12\",\"commitmentsByPath\":[\"0x23e91aed573939b924d6c2b19adbc8ed0e90a390abb89f4c003d5c37720920b6\",\"0x0950d498fb25f568fe9200f51c19aeeba641ae05a6551d3bd1570b1a596c6ff9\",\"0x2bb313f6892485203ea803c3ca707a28da133a785d5e62d579cecf93f9ae8c6d\"],\"d\":\"0x4dfcc1ba12555737283177db78ae874c319e0c4061d7b55d2db521d2db178626\",\"ipaProof\":{\"cl\":[\"0x708cb3182840dc0db76d50ba20c4a0941ab6b1ba3c5157fa2b4f144526bffb95\",\"0x51f2af40e0ff38c97659f8b6090f261904a85312247e0a356f2d8791c22f29ff\",\"0x0133480d9ca599acc32fc73fe3d45d3361487d0b2f7471aedbe882f5851e2ada\",\"0x5067c7da0e2002583be289a01bc5410ee49bf05f8665bbc81c693a0792563db1\",\"0x00e2fa96e5bd811f6c97ab36a75820d59a3bc1cb33373704c4972f5d2eb22112\",\"0x093caa30fc8acab0e7e95f74a051a19674e6aacb6f27fd8032f055e89912221c\",\"0x3c865d1a327ac3170c28a1059ae41920de9bf850fc6aa1d8d7eb0566586018b2\",\"0x3df3ce2e070715630b563dd5fa4efe3a4c5bf4a9d5ac63f75e8b606533be7a3f\"],\"cr\":[\"0x2a762922866c5958264de46a7160f6a30be30c4b5e0bffbb97b19818c33802c8\",\"0x5ec0c71b9d81f7bd150024d338b09d07c6f7d1f7c495bdc1ddf86300db0abfbc\",\"0x3978d4d085a4431e6f30a629257427b708edf49dcef8bfa3210e56e9f6e36068\",\"0x4c8fabddca665c141cc0ab2b67b139712f0af8a87c4751a58bc9872f87a19c28\",\"0x0f11a365729f353cb46bf9a52fa33285c501be3a1c82a0387bab811ea0b673e8\",\"0x6b5a002bbae5c63a94fa468a55bebe57f134db8190f92768541dcc7dbeee350a\",\"0x4236ab6a5172f893fb9d4cd813fb53d7401b54ef9714300e4e155420019bd215\",\"0x3b6f380b908352b35d7e65493c92ef1dc78ad135441b3bbebe501843cc47854d\"],\"finalEvaluation\":\"0x1b6790e37ebd5cc81444cbca6af83eaf0385d2ddbca14ef630d266c7a650b80c\"}}}}";
        IJsonSerializer serializer = new EthereumJsonSerializer();
        ExecutionPayload? result = serializer.Deserialize<ExecutionPayload>(payload);
        Console.WriteLine(result);
        Console.WriteLine(serializer.Serialize(result));
    }


}