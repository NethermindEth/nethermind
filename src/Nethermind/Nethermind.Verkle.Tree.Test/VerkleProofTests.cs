// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Verkle;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Logging;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Tree.Proofs;

namespace Nethermind.Verkle.Tree.Test;

public class VerkleProofTest
{
    [Test]
    public void TestProofVerifyTwoLeaves()
    {
        byte[][] keys =
        {
            VerkleTestUtils._emptyArray,
            VerkleTestUtils._arrayAll0Last1,
            VerkleTestUtils._maxValue,
        };
        VerkleTree tree = VerkleTestUtils.CreateVerkleTreeWithKeysAndValues(keys, keys);
        VerkleProof proof = tree.CreateVerkleProof(keys, out Banderwagon root);

        bool verified = VerkleTree.VerifyVerkleProof(proof, new List<byte[]>(keys), new List<byte[]?>(keys), root, out _);
        Assert.That(verified, Is.True);
    }

    [Test]
    public void TestVerkleProof()
    {
        List<byte[]> keys = new()
        {
            VerkleTestUtils._keyVersion,
            VerkleTestUtils._keyBalance,
            VerkleTestUtils._keyNonce,
            VerkleTestUtils._keyCodeCommitment,
            VerkleTestUtils._keyCodeSize
        };

        List<byte[]> values = new()
        {
            VerkleTestUtils._emptyArray,
            VerkleTestUtils._emptyArray,
            VerkleTestUtils._emptyArray,
            VerkleTestUtils._valueEmptyCodeHashValue,
            VerkleTestUtils._emptyArray
        };
        VerkleTree tree = VerkleTestUtils.CreateVerkleTreeWithKeysAndValues(keys.ToArray(), values.ToArray());

        VerkleProof proof = tree.CreateVerkleProof(keys.ToArray(), out Banderwagon root);
        bool verified = VerkleTree.VerifyVerkleProof(proof, keys, values, root, out _);
        Assert.That(verified, Is.True);
    }

    [Test]
    public void BasicProofTrue()
    {
        VerkleTree tree = VerkleTestUtils.GetVerkleTreeForTest(DbMode.MemDb);

        byte[][] keys =
        {
            new byte[]{0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0},
            new byte[]{1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0},
            new byte[]{2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0},
            new byte[]{3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0}
        };

        tree.Insert(keys[0], keys[0]);
        tree.Insert(keys[1], keys[1]);
        tree.Insert(keys[2], keys[2]);
        tree.Insert(keys[3], keys[3]);
        tree.Commit();
        tree.CommitTree(0);

        VerkleProof proof = tree.CreateVerkleProof(keys, out Banderwagon root);

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
        VerkleTree tree = VerkleTestUtils.GetVerkleTreeForTest(DbMode.MemDb);

        byte[][] keys =
        {
            new byte[]{0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0},
            new byte[]{0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1},
            new byte[]{0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2},
            new byte[]{0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3}
        };

        tree.Insert(keys[0], keys[0]);
        tree.Insert(keys[1], keys[1]);
        tree.Insert(keys[2], keys[2]);
        tree.Insert(keys[3], keys[3]);
        tree.Commit();
        tree.CommitTree(0);

        VerkleProof proof = tree.CreateVerkleProof(keys, out Banderwagon root);

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
        VerkleTree tree = VerkleTestUtils.GetVerkleTreeForTest(DbMode.MemDb);

        byte[][] keys =
        {
            new byte[]
            {
                3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3
            },
        };

        VerkleProof proof = tree.CreateVerkleProof(keys, out Banderwagon root);

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
        VerkleTree tree = new VerkleTree(db, LimboLogs.Instance);
        byte[][] keys = new byte[1000][];
        for (int i = 0; i < 1000; i++)
        {
            keys[i] = TestItem.GetRandomKeccak().Bytes.ToArray();
            tree.Insert(keys[i], Keccak.Zero.Bytes);
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

        Console.WriteLine("Elapsed={0}",sw.Elapsed/iteration);
    }

    [Test]
    public void RandomProofCalculationAndVerification()
    {
        Random rand = new(0);
        IDbProvider db = VerkleDbFactory.InitDatabase(DbMode.MemDb, null);
        VerkleTree tree = new(db, LimboLogs.Instance);
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
            tree.Insert(keys[i],  values[i]);
            tree.Commit();
        }
        tree.CommitTree(0);

        VerkleProof proof = tree.CreateVerkleProof(keys[..500], out Banderwagon root);
        bool verified = VerkleTree.VerifyVerkleProof(proof, new(keys[..500]),
            new(values[..500]), root, out _);
        Assert.That(verified, Is.True);
    }


}
