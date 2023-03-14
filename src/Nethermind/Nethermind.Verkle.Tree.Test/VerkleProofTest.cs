// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Db.Rocks;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Tree;
using Nethermind.Verkle.Tree.Proofs;
using NUnit.Framework;

namespace Nethermind.Verkle.Tree.Test;

public class VerkleProofTest
{
    [Test]
    public void TestVerkleProof()
    {
        VerkleTree tree = VerkleTestUtils.GetVerkleTreeForTest(DbMode.MemDb);

        tree.Insert(VerkleTestUtils._keyVersion, VerkleTestUtils._emptyArray);
        tree.Insert(VerkleTestUtils._keyBalance, VerkleTestUtils._emptyArray);
        tree.Insert(VerkleTestUtils._keyNonce, VerkleTestUtils._emptyArray);
        tree.Insert(VerkleTestUtils._keyCodeCommitment, VerkleTestUtils._valueEmptyCodeHashValue);
        tree.Insert(VerkleTestUtils._keyCodeSize, VerkleTestUtils._emptyArray);
        tree.Flush(0);

        VerkleProver prover = new VerkleProver(tree._stateDb);
        prover.CreateVerkleProof(new List<byte[]>(new[]
            {VerkleTestUtils._keyVersion, VerkleTestUtils._keyNonce, VerkleTestUtils._keyBalance, VerkleTestUtils._keyCodeCommitment}), out Banderwagon _);

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
        tree.Flush(0);

        VerkleProver prover = new VerkleProver(tree._stateDb);
        VerkleProof proof = prover.CreateVerkleProof(new List<byte[]>(keys), out Banderwagon root);

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
        (bool, UpdateHint?) verified = Verifier.VerifyVerkleProof(proof, new List<byte[]>(keys), new List<byte[]?>(keys), root);
        Assert.That(verified.Item1, Is.True);
    }
}
