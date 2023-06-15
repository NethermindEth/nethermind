// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus.Clique;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using NUnit.Framework;
using BlockTree = Nethermind.Blockchain.BlockTree;

namespace Nethermind.Clique.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class SnapshotManagerTests
    {
        private IDb _snapshotDb = new MemDb();

        private const string Block1Rlp = "f9025bf90256a06341fd3daf94b748c72ced5a5b26028f2474f5f00d824504e4fa37a75767e177a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a053580584816f617295ea26c0e17641e0120cab2f0a8ffb53a866fd53aa8e8c2da056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b901000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000002018347c94c808458ee45dab861d783010600846765746887676f312e372e33856c696e757800000000000000009f1efa1efa72af138c915966c639544a0255e6288e188c22ce9168c10dbe46da3d88b4aa065930119fb886210bf01a084fde5d3bc48d8aa38bca92e4fcc5215100a00000000000000000000000000000000000000000000000000000000000000000880000000000000000c0c0";
        private const string Block2Rlp = "f9025bf90256a0a7684ac44d48494670b2e0d9085b7750e7341620f0a271db146ed5e70c1db854a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a053580584816f617295ea26c0e17641e0120cab2f0a8ffb53a866fd53aa8e8c2da056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b901000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000002028347db3d808458ee45eab861d783010600846765746887676f312e372e33856c696e75780000000000000000b5a4a624d2e19fdab62ff7f4d2f2b80dfab4c518761beb56c2319c4224dd156f698bb1a2750c7edf12d61c4022079622062039637f40fb817e2cce0f0a4dae9c01a00000000000000000000000000000000000000000000000000000000000000000880000000000000000c0c0";
        private const string Block3Rlp = "f9025bf90256a09b095b36c15eaf13044373aef8ee0bd3a382a5abb92e402afa44b8249c3a90e9a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a053580584816f617295ea26c0e17641e0120cab2f0a8ffb53a866fd53aa8e8c2da056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b901000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000002038347e7c4808458ee45f9b861d783010600846765746887676f312e372e33856c696e757800000000000000004e10f96536e45ceca7e34cc1bdda71db3f3bb029eb69afd28b57eb0202c0ec0859d383a99f63503c4df9ab6c1dc63bf6b9db77be952f47d86d2d7b208e77397301a00000000000000000000000000000000000000000000000000000000000000000880000000000000000c0c0";
        private const string Block4Rlp = "f9025bf90256a09eb9db9c3ec72918c7db73ae44e520139e95319c421ed6f9fc11fa8dd0cddc56a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a053580584816f617295ea26c0e17641e0120cab2f0a8ffb53a866fd53aa8e8c2da056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b901000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000002048347e7c4808458ee4608b861d783010600846765746887676f312e372e33856c696e75780000000000000000713c53f21fd59a94de9c3f8342777f6660a3e99187114ebf52f0127caf6bcefa77195308fb80b4e6223673757732485c234d8f431a99c46799c57a4ecc4e4e5401a00000000000000000000000000000000000000000000000000000000000000000880000000000000000c0c0";
        private const string Block5Rlp = "f9025bf90256a08dabb64040467fa4e99a061878d90396978d173ecf47b2f72aa31e8d7ad917a9a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a053580584816f617295ea26c0e17641e0120cab2f0a8ffb53a866fd53aa8e8c2da056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b901000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000002058347e7c4808458ee4617b861d783010600846765746887676f312e372e33856c696e7578000000000000000052ad0baf5fefa05b3a51cdcc6484901465c66be48c3c9b7a4fcb5fcb867ea220390bcb6e4d740bc17d0c9e948cf0803cab107b538fb3a3efde89e26ede9ee26801a00000000000000000000000000000000000000000000000000000000000000000880000000000000000c0c0";

        private readonly Address _signer1 = new("0x7ffc57839b00206d1ad20c69a1981b489f772031");
        private readonly Address _signer2 = new("0xb279182d99e65703f0076e4812653aab85fca0f0");
        private readonly Address _signer3 = new("0x42eb768f2244c8811c63729a21a3569731535f06");

        private BlockTree _blockTree;

        [OneTimeSetUp]
        public void Setup_chain()
        {
            // Import blocks
            _blockTree = Build.A.BlockTree().TestObject;
            Block block1 = Rlp.Decode<Block>(new Rlp(Bytes.FromHexString(Block1Rlp)));
            Block block2 = Rlp.Decode<Block>(new Rlp(Bytes.FromHexString(Block2Rlp)));
            Block block3 = Rlp.Decode<Block>(new Rlp(Bytes.FromHexString(Block3Rlp)));
            Block block4 = Rlp.Decode<Block>(new Rlp(Bytes.FromHexString(Block4Rlp)));
            Block block5 = Rlp.Decode<Block>(new Rlp(Bytes.FromHexString(Block5Rlp)));
            Block genesisBlock = CliqueTests.GetRinkebyGenesis();
            // Add blocks
            MineBlock(_blockTree, genesisBlock);
            MineBlock(_blockTree, block1);
            MineBlock(_blockTree, block2);
            MineBlock(_blockTree, block3);
            MineBlock(_blockTree, block4);
            MineBlock(_blockTree, block5);
        }

        [Test]
        public void Creates_new_snapshot()
        {
            SnapshotManager snapshotManager = new(CliqueConfig.Default, _snapshotDb, _blockTree, NullEthereumEcdsa.Instance, LimboLogs.Instance);
            Block genesis = CliqueTests.GetRinkebyGenesis();
            Snapshot snapshot = snapshotManager.GetOrCreateSnapshot(0, genesis.Hash);
            Assert.That(snapshot.Hash, Is.EqualTo(genesis.Hash));
        }

        [Test]
        public void Loads_snapshot()
        {
            SnapshotManager snapshotManager = new(CliqueConfig.Default, _snapshotDb, _blockTree, NullEthereumEcdsa.Instance, LimboLogs.Instance);
            Block genesis = CliqueTests.GetRinkebyGenesis();
            Snapshot snapshot = snapshotManager.GetOrCreateSnapshot(0, genesis.Hash);
            Assert.NotNull(snapshot);
            Assert.That(snapshot.Hash, Is.EqualTo(genesis.Hash));
            Assert.That(snapshot.Number, Is.EqualTo(genesis.Number));
            // Check signers
            Assert.IsTrue(snapshot.Signers.ContainsKey(_signer1));
            Assert.IsTrue(snapshot.Signers.ContainsKey(_signer2));
            Assert.IsTrue(snapshot.Signers.ContainsKey(_signer3));
        }

        [Test]
        public void Can_calculate_clique_header_hash()
        {
            BlockHeader header = BuildCliqueBlock();

            Keccak expectedHeaderHash = new("0x7b27b6add9e8d0184c722dde86a2a3f626630264bae3d62ffeea1585ce6e3cdd");
            Keccak headerHash = SnapshotManager.CalculateCliqueHeaderHash(header);
            Assert.That(headerHash, Is.EqualTo(expectedHeaderHash));
        }

        [Test]
        public void Recognises_signer_turn()
        {
            SnapshotManager snapshotManager = new(CliqueConfig.Default, _snapshotDb, _blockTree, NullEthereumEcdsa.Instance, LimboLogs.Instance);
            Block genesis = CliqueTests.GetRinkebyGenesis();
            Snapshot snapshot = snapshotManager.GetOrCreateSnapshot(0, genesis.Hash);
            SnapshotManager manager = new(CliqueConfig.Default, _snapshotDb, _blockTree, new EthereumEcdsa(BlockchainIds.Goerli, LimboLogs.Instance), LimboLogs.Instance);
            // Block 1
            Assert.IsTrue(manager.IsInTurn(snapshot, 1, _signer1));
            Assert.IsFalse(manager.IsInTurn(snapshot, 1, _signer2));
            Assert.IsFalse(manager.IsInTurn(snapshot, 1, _signer3));
            // Block 2
            Assert.IsFalse(manager.IsInTurn(snapshot, 2, _signer1));
            Assert.IsTrue(manager.IsInTurn(snapshot, 2, _signer2));
            Assert.IsFalse(manager.IsInTurn(snapshot, 2, _signer3));
            // Block 3
            Assert.IsFalse(manager.IsInTurn(snapshot, 3, _signer1));
            Assert.IsFalse(manager.IsInTurn(snapshot, 3, _signer2));
            Assert.IsTrue(manager.IsInTurn(snapshot, 3, _signer3));
        }

        private static BlockHeader BuildCliqueBlock()
        {
            BlockHeader header = Build.A.BlockHeader
                .WithParentHash(new Keccak("0x6d31ab6b6ee360d075bb032a094fb4ea52617268b760d15b47aa439604583453"))
                .WithUnclesHash(Keccak.OfAnEmptySequenceRlp)
                .WithBeneficiary(Address.Zero)
                .WithBloom(Bloom.Empty)
                .WithStateRoot(new Keccak("0x9853b6c62bd454466f4843b73e2f0bdd655a4e754c259d6cc0ad4e580d788f43"))
                .WithTransactionsRoot(PatriciaTree.EmptyTreeHash)
                .WithReceiptsRoot(PatriciaTree.EmptyTreeHash)
                .WithDifficulty(2)
                .WithNumber(269)
                .WithGasLimit(4712388)
                .WithGasUsed(0)
                .WithTimestamp(1492014479)
                .WithExtraData(Bytes.FromHexString("0xd783010600846765746887676f312e372e33856c696e757800000000000000004e2b663c52c4c1ef0db29649f1f4addd93257f33d6fe0ae6bd365e63ac9aac4169e2b761aa245fabbf0302055f01b8b5391fa0a134bab19710fd225ffac3afdf01"))
                .WithMixHash(Keccak.Zero)
                .WithNonce(0UL)
                .TestObject;
            return header;
        }

        private void MineBlock(BlockTree tree, Block block)
        {
            tree.SuggestBlock(block);
            tree.UpdateMainChain(block);
        }
    }
}
