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
        VerkleProof proof = tree.CreateVerkleProof(keys, out Banderwagon root);
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

        VerkleProof proof = tree.CreateVerkleProof(keys.ToArray(), out Banderwagon root);
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

        VerkleProof proof = tree.CreateVerkleProof(keys, out Banderwagon root);
        TestProofSerialization(proof);


        const string expectedProof = "00000000040000000a0a0a0a0800000056778fe0bcf12a14820d4c054d85cfcae4bdb7017107b6769cecd42629a3825e38f30e21c" +
                                     "79747190371df99e88b886638be445d44f8f9b56ca7c062ea3299446c650ce85c8b5d3cb5ccef8be82858aa2fa9c2cad512086db5" +
                                     "21bd2823e3fc38107802129c490edadbab32ec891ee6310e4f4f00e0056ce3bb0ffc6840a27577556a6fa8933ab25ddb0fb102fca" +
                                     "932ac58a5f539f83b9bfb6e21ea742aa5ad7403f36f9c0821d7014e7a7b917c1d3b72acf724906a30a8fefb09889c3e4cfbc528a4" +
                                     "0fd3331b653dea7be3fe55bc1a0451e0dbad672ec0236cac46e5b23d13d5562743b585beaf3dc1987c9bdf5701af9c4784a392549" +
                                     "9bd6318b63ccec4b35f1bd427779680f60c2df48a9df458fa989fc0f8a7a38d54363b722138e4e4ac4351c5a0aa5cc5e53b697d8b" +
                                     "57eaa43db3dc3987f9f1e71c31b5098721dad2910465fff50d7fb4d0145c41c53a395f99052a251fcb97ef31da582938a67756697" +
                                     "024068f61bd61a10a2c7d8d2a522fa3834e1516f16bfc4ec7f1808069effeab095a5ff89d9bacad138316aa7c9001ce03830e443d" +
                                     "a2aed1f66b5211ae7912bbe751bb05960d4f6bcdb3d266685d6e1b81c632e66f90df80b76cfe8e619bb29ed3322c2f9743d918f47" +
                                     "062f4d077d5a658ab41c3d9c3add6def200e7f242d5ed840a7389ec6a7ab71f6ce813fb898a530af1a3c800f849bf56aae0c7a12a" +
                                     "f1c0ee210863a29533a0c848de893cd1bc0256d8b3ddd3439ee55bc94eb77f71ac2d994b4fd1f08738f53183ac85b3c6e4ee1f8e9" +
                                     "7e0154df668ec700131d4167b93d6180ed760ded7c1899f6f53116ea6c9b54ab809809ae05e821c2e4b0b3cccbf6d643f5aff2dd6" +
                                     "ea235f2e53efccd6009f560e1c0eb01163e1415b2176a2679f8a3845884f3ffac354449be949b849325ec0d66af841825dbf6bd66" +
                                     "8bb91a49c150be9b911a60e285c2ffa50f0380bcb86ed85bf7114c2c0d0aa8e7e6fb33351464a9de74b4219ebf351933831d1f5b5" +
                                     "3467f856adfa7b478c428027dd408f61ff4eb9d94d0ee8c3e79e0265b0635af17db6aa7ca1b463b70e4c51fffb7f8403c94c9315a" +
                                     "7b48d8a11ffd23510e0936842ae8368dedfb511a01dfc930c96d8ee26235b4acc8ace6a0d8fc3fb9142b69b2b989f97ce36ba4386" +
                                     "8d93add3abe7a012";

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

        VerkleProof proof = tree.CreateVerkleProof(keys, out Banderwagon root);
        TestProofSerialization(proof);

        const string expectedProof = "00000000010000000a020000000b2cd97f2703f0e0030f8356c66ef9cda8587109aab48ebdf02fd49ceefa716d1731296" +
                                     "d27f24eddf8e4576bcf69395373a282be54e0d16966c5ac77f3423b9f05e4ab9d3080bed0f9d2c6eaedb998ae66bd1cda" +
                                     "c02572f57f0f2f4ea61621e12ef7f19003b84b8e4b35ade794a687661cc6f9f96c8b6915f5b82c47525bc8e327e411d55" +
                                     "3fcecc9a4ff9979a60e66283cc7f5e3677ec7b7c18d5fecfaa11fe2446de5c2a55d5f577bc030ed5e4b70578d87263f2a" +
                                     "05956f70e9e43dd6085a231435b1af4c59950e2dd6cce694dbd44e47c6d0ed560e886d2b137d8962d5fd992ebbbbc4843" +
                                     "aebcbe8f982f8439c91423c41f08e4dba677b3032ccec61bc2fe759df90164cc2b3fa5dd2db8da1baf0991805755a45f1" +
                                     "44b6c163302695e2426739b67afc46e1ccaa67cc903c78009bef1d5983d7e027c443f9d785d63e8e25690b04d0c856699" +
                                     "97e44442bbdeca25f074d79030ddb4b98cefbdfa49e663628a76210c2573ab8b20e77d202b54ac9541d0c2ed7985aca3b" +
                                     "10e5b742edd6b071065cf195545d69cebe14391025b460e79d12d32f2cb61adf99603198931637fffd2d999e19495ea88" +
                                     "95ee3480e2d5b39006b4c19429b100d28c214f016407c599c5dd20ce3f4c5d59ee6817cf8fd65a879519175e5c1d3d931" +
                                     "be44041833bf32fd3d31d28304010e4451565ad226b86a0d24b27b23c3a486cea63d58297a0179dd32134e6e80d85d3d3" +
                                     "022a01ada9aea52c542fd1d20cc47af6a8462c11390b9ff6fae91943fe7c0d86c19bce4ffdcf34bb6a03c94f5de3d5c18" +
                                     "a49048e06d9b8f3d481ada5b82c8b8a9619d538fa039bb99332f6395f387acee1e577bfe092dc02094949c12a8e0d580f" +
                                     "6390d9a41302df30ffaf57e40296c75052bb028fe2b09";

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

        VerkleProof proof = tree.CreateVerkleProof(keys, out Banderwagon root);
        TestProofSerialization(proof);


        const string expectedProof = "000000000100000008000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
                                     "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
                                     "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
                                     "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
                                     "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
                                     "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
                                     "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
                                     "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
                                     "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
                                     "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
                                     "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
                                     "00000000000000000000000000000000000000000000000000000000";

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

        VerkleProof proof = tree.CreateVerkleProof(keys[..500], out Banderwagon root);
        TestProofSerialization(proof);
        bool verified = VerkleTree.VerifyVerkleProof(proof, new(keys[..500]),
            new(values[..500]), root, out _);
        Assert.That(verified, Is.True);
    }

    private void TestProofSerialization(VerkleProof proof)
    {
        VerkleProofSerializer ser = new();
        var stream = new RlpStream(Rlp.LengthOfSequence(ser.GetLength(proof, RlpBehaviors.None)));
        ser.Encode(stream, proof);
        VerkleProof data = ser.Decode(new RlpStream(stream.Data!));
        data.ToString().Should().BeEquivalentTo(proof.ToString());
    }

    [Test]
    public void TestBlock257()
    {
        string payload = "{\"parentHash\":\"0x4ff50e1454f9a9f56871911ad5b785b7f9966cce3cb12eb0e989332ae2279213\",\"feeRecipient\":\"0xf97e180c050e5ab072211ad2c213eb5aee4df134\",\"stateRoot\":\"0x5bf12e46f1ce048f74229eb6fb4bbdb715eb615e0b79abf32a53712a9f643de7\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"prevRandao\":\"0xb5f43407166c107f53f501a3409e3b74cca112cc60c2bdc169d4d1476461721d\",\"blockNumber\":\"0x101\",\"gasLimit\":\"0x1c9c380\",\"gasUsed\":\"0x0\",\"timestamp\":\"0x65c21634\",\"extraData\":\"0xd983010c01846765746889676f312e32302e3133856c696e7578\",\"baseFeePerGas\":\"0x7\",\"blockHash\":\"0xbedc42451ae09174af2a141ca36bee23337f3cdf6036ca68358a3decc1454057\",\"transactions\":[],\"withdrawals\":[],\"executionWitness\":{\"stateDiff\":[{\"stem\":\"0x117b67dd491b9e11d9cde84ef3c02f11ddee9e18284969dc7d496d43c300e5\",\"suffixDiffs\":[{\"suffix\":0,\"currentValue\":null,\"newValue\":\"0x4ff50e1454f9a9f56871911ad5b785b7f9966cce3cb12eb0e989332ae2279213\"}]}],\"verkleProof\":{\"otherStems\":[\"0x1123356d04d4bd662ba38c44cbd79d4108521284d80327fa533e0baab1af9f\"],\"depthExtensionPresent\":\"0x09\",\"commitmentsByPath\":[\"0x3514e85936b1e484f84fc93b58e38b9ed9b0d870002653085f9dd758b1b8c3f5\"],\"d\":\"0x59a7b2bd71eff71d6d43abd61c1533246dce01a1b973c9287e2c9b6dea76c21a\",\"ipaProof\":{\"cl\":[\"0x07c7bc1903217d5eb06e067cd535376c7fa737e717a06cd62686e9dcebbef0a2\",\"0x11ca24317a6772c962252e1d1963450027c5024152aef68b148fb833e30c0a99\",\"0x0c330e01ce8c356e2ad9ba15f3ac4d730e5f986bf0f227b28c55ccdbfb373674\",\"0x380b8e7d511dcd37d9596c084d156eaff21eec9368745c1222b4bfc2ce231db8\",\"0x27c74c37d499e3475f4ad8968417e72b068ba923ffde27e21d323c29fe30ce78\",\"0x5d2ebbdb282ccc9aa7f878a8875b01072e7444a02ee7f37daefe7745dc95ad32\",\"0x494d512c207264ec018ca26d137bb46d6e9c68c934ae40d8c3d55024877a68f2\",\"0x1b73f07a94186b7c3221d46e2082049e79c3b90999c9fc61970d9f2fbd5fbf96\"],\"cr\":[\"0x6bbdeac4b34f9c03ea3d7d194702f7da05b3067609a0b4f163280f39a20ae781\",\"0x596e6fdf00232028cdc19ad3830920b4fb685993031288355b4858280f14071b\",\"0x2b8a39633e7a69c80c436f1250175476ba5a5dff2432e246f5478d5ba68b0903\",\"0x2b5a3ad002ddff1d649ef4f03ea053baac62f4cd038fd391aff527f73bdd568a\",\"0x409f15bc99f774b2a0f5b2ea4a06c1d7779d24776d013bc93f99d264f558b35f\",\"0x49903ee86b289a82033d7887aeecdefd60ed390fb470252ca0101619377ec1eb\",\"0x254ad6cf40e599e207698c6fe438d51b221336f0a6ee3297b7a2113fc09b37b4\",\"0x3bad9aabbf4dc90e786d1b69c9c87e63974a376067029ed5e3d7fa04b701fd96\"],\"finalEvaluation\":\"0x1349fd2896502fe7778d871bf10c4b7820744255178b429c08512c64c6e96d2c\"}}}}";
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
