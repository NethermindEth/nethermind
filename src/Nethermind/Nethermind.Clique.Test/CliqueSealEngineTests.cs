// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Clique;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Db.Blooms;
using Nethermind.Trie;
using Nethermind.Wallet;
using NUnit.Framework;
using BlockTree = Nethermind.Blockchain.BlockTree;

namespace Nethermind.Clique.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class CliqueSealEngineTests
    {
        private const string Block1Rlp = "f9025bf90256a0bc3546bbc73f86a96f5d966965ded9873dddc2278ce48c7251ef3062620a3543a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a053580584816f617295ea26c0e17641e0120cab2f0a8ffb53a866fd53aa8e8c2da056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b901000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000002018347c94c808458ee45dab861d783010600846765746887676f312e372e33856c696e75780000000000000000a06daa01cc93a2875795a705f8eae4d00de10d513bf975515a12e545254ae3195be42d6c49a83ae580f8c7ff7c69eef4f16626e8fd873114b46edec7744edca400a00000000000000000000000000000000000000000000000000000000000000000880000000000000000c0c0";
        private const string Block2Rlp = "f9025bf90256a03a060a78114b179fffc5581fbf36668f39a9928e58eaaf693c971b32446bd376a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a053580584816f617295ea26c0e17641e0120cab2f0a8ffb53a866fd53aa8e8c2da056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b901000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000002028347db3d808458ee45eab861d783010600846765746887676f312e372e33856c696e75780000000000000000e237c51d592ede43af886756f5ef941ae195bcfc2c324a206e753e070e3174a7649bc0ce5e17c94ecae5087fc6796c02a231fdfb94e4c9b3d1e19dd9bdb2040800a00000000000000000000000000000000000000000000000000000000000000000880000000000000000c0c0";
        private const string Block3Rlp = "f9025bf90256a012d6551b449ce661299860948eccc6d65b26a67deef09d408ac6dfce1fac995aa01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a053580584816f617295ea26c0e17641e0120cab2f0a8ffb53a866fd53aa8e8c2da056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b901000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000002038347e7c4808458ee45f9b861d783010600846765746887676f312e372e33856c696e7578000000000000000021dfd43d2956e78e06eaf747f43858d442a79c970a109a6c39385bee232996b64a1de72743fa56efc967fd19cb9308174340f94a2f07b96eb82f82a3c72ce86300a00000000000000000000000000000000000000000000000000000000000000000880000000000000000c0c0";
        private const string Block4Rlp = "f9025bf90256a01ef022156ff2d5730931028d3fddaccf9ccda8818ba7f79dbeea78302550824ca01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a053580584816f617295ea26c0e17641e0120cab2f0a8ffb53a866fd53aa8e8c2da056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b901000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000002048347e7c4808458ee4608b861d783010600846765746887676f312e372e33856c696e7578000000000000000095ed0b3d04835d9ccbf11f79fdf75cf01a4a79fcb4b417b00acf461306e565616ed1903b307407143f2393af49d892399e8b09e01728d419f900ae99e13437f000a00000000000000000000000000000000000000000000000000000000000000000880000000000000000c0c0";
        private const string Block5Rlp = "f9025bf90256a017d9b71cd90b4381da41265491792f5f78d36f80b14c3c7073af470d7e9b882aa01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a053580584816f617295ea26c0e17641e0120cab2f0a8ffb53a866fd53aa8e8c2da056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b901000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000002058347e7c4808458ee4617b861d783010600846765746887676f312e372e33856c696e75780000000000000000840db225f54032162d91245cc2f6c1b80b2e1d2b0657c3707e9c1f46add7b7525e0ba267addc476724e1eb70f7f76a5fd8c95df3d82dfb7a3d8c334c2fcb5ad201a00000000000000000000000000000000000000000000000000000000000000000880000000000000000c0c0";

        private readonly List<PrivateKey> _signers = new()
        {
            new PrivateKey("06E84833EAC809859F46F84311CB152E2D2A505FE6B5FBC4CD2CABD37B678F1C"),
            new PrivateKey("C7B39D4F871ACA337E3CC3AB956F1A916B5EF23AF9F5571566DDB8D3C99F66AC"),
            new PrivateKey("9BD8E918E3176E86D406BFCE261D4CD2589167E3DBD0236B08B0B285783D7553"),
            new PrivateKey("7DC56B10FD1EC64A8BF7547D3BAA254ACB96E8F2AD5A006DD2EBF9C40409A2CE")
        };
        private CliqueSealer _clique;
        private CliqueSealValidator _sealValidator;
        private SnapshotManager _snapshotManager;
        private Block _lastBlock;
        private PrivateKey _currentSigner;
        private BlockTree _blockTree;

        [OneTimeSetUp]
        public void Setup_chain()
        {
            IDb db = new MemDb();
            // Import blocks
            _blockTree = Build.A.BlockTree().TestObject;
            Block genesisBlock = GetRinkebyGenesis();
            MineBlock(_blockTree, genesisBlock);

            Block block1 = Rlp.Decode<Block>(new Rlp(Bytes.FromHexString(Block1Rlp)));
            block1.Header.ParentHash = genesisBlock.Hash;
            BuildSealer(1, db).SealBlock(block1, CancellationToken.None).Wait();
            block1.Header.Hash = block1.CalculateHash();
            MineBlock(_blockTree, block1);

            Block block2 = Rlp.Decode<Block>(new Rlp(Bytes.FromHexString(Block2Rlp)));
            block2.Header.ParentHash = block1.Hash;
            BuildSealer(2, db).SealBlock(block2, CancellationToken.None).Wait();
            block2.Header.Hash = block2.CalculateHash();
            MineBlock(_blockTree, block2);

            Block block3 = Rlp.Decode<Block>(new Rlp(Bytes.FromHexString(Block3Rlp)));
            block3.Header.ParentHash = block2.Hash;
            BuildSealer(3, db).SealBlock(block3, CancellationToken.None).Wait();
            block3.Header.Hash = block3.CalculateHash();
            MineBlock(_blockTree, block3);

            Block block4 = Rlp.Decode<Block>(new Rlp(Bytes.FromHexString(Block4Rlp)));
            block4.Header.ParentHash = block3.Hash;
            BuildSealer(4, db).SealBlock(block4, CancellationToken.None).Wait();
            block4.Header.Hash = block4.CalculateHash();
            MineBlock(_blockTree, block4);

            Block block5 = Rlp.Decode<Block>(new Rlp(Bytes.FromHexString(Block5Rlp)));
            block5.Header.ParentHash = block4.Hash;
            BuildSealer(5, db).SealBlock(block5, CancellationToken.None).Wait();
            block5.Header.Hash = block5.CalculateHash();
            MineBlock(_blockTree, block5);
            _lastBlock = block5;
            // Init snapshot db


            // Select in-turn signer
            int currentBlock = 6;

            BuildSealer(currentBlock, db);
        }

        private CliqueSealer BuildSealer(int currentBlock, IDb db)
        {
            IEthereumEcdsa ecdsa = new EthereumEcdsa(BlockchainIds.Rinkeby, LimboLogs.Instance);
            CliqueConfig config = new();
            int currentSignerIndex = (currentBlock % _signers.Count);
            _currentSigner = _signers[currentSignerIndex];
            _snapshotManager = new SnapshotManager(config, db, _blockTree, ecdsa, LimboLogs.Instance);
            _sealValidator = new CliqueSealValidator(config, _snapshotManager, LimboLogs.Instance);
            _clique = new CliqueSealer(new Signer(BlockchainIds.Rinkeby, _currentSigner, LimboLogs.Instance), config,
                _snapshotManager, LimboLogs.Instance);
            return _clique;
        }

        [Test]
        public async Task Can_sign_block()
        {
            Block block6 = CreateBlock(2, 6, _lastBlock);
            Block signed = await _clique.SealBlock(block6, CancellationToken.None);
            bool validHeader = _sealValidator.ValidateParams(_blockTree.FindHeader(signed.ParentHash, BlockTreeLookupOptions.None), signed.Header);
            bool validSeal = _sealValidator.ValidateSeal(signed.Header, true);
            Assert.True(validHeader);
            Assert.True(validSeal);
        }

        private Block GetRinkebyGenesis()
        {
            Keccak parentHash = Keccak.Zero;
            Keccak unclesHash = Keccak.OfAnEmptySequenceRlp;
            Address beneficiary = Address.Zero;
            UInt256 difficulty = new(1);
            long number = 0L;
            int gasLimit = 4700000;
            ulong timestamp = 1492009146UL;
            byte[] extraData = Bytes.FromHexString(GetGenesisExtraData());
            BlockHeader header = new(parentHash, unclesHash, beneficiary, difficulty, number, gasLimit, timestamp, extraData);
            header.Bloom = Bloom.Empty;
            Block genesis = new(header);
            genesis.Header.Bloom = Bloom.Empty;
            genesis.Header.Hash = genesis.CalculateHash();

            // this would need to be loaded from rinkeby chainspec to include allocations
            //            Assert.AreEqual(new Keccak("0x6341fd3daf94b748c72ced5a5b26028f2474f5f00d824504e4fa37a75767e177"), genesis.Hash);

            genesis.Header.Hash = genesis.Header.CalculateHash();
            return genesis;
        }

        private string GetGenesisExtraData()
        {
            StringBuilder extraDataString = new();
            extraDataString.Append("52657370656374206d7920617574686f7269746168207e452e436172746d616e");
            for (int i = 0; i < _signers.Count; i++)
            {
                extraDataString.Append(_signers[i].Address.ToString(false, false));
            }

            extraDataString.Append("0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000");
            return extraDataString.ToString();
        }

        private static void MineBlock(BlockTree tree, Block block)
        {
            tree.SuggestBlock(block);
        }

        private Block CreateBlock(int blockDifficulty, int blockNumber, Block lastBlock)
        {
            Keccak parentHash = lastBlock.Hash;
            Keccak unclesHash = Keccak.OfAnEmptySequenceRlp;
            Address beneficiary = Address.Zero;
            UInt256 difficulty = (UInt256)blockDifficulty;
            long number = blockNumber;
            int gasLimit = 4700000;
            ulong timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            byte[] extraData = Bytes.FromHexString("d883010812846765746888676f312e31312e31856c696e75780000000000000028eb026ab5355b45499053382886754f1db544618d45edc979de1864d83a626b77513bd34d7f21059e79e303c3ab210e1424e71bcb8347835cbd378a785a06f800");
            BlockHeader header = new(parentHash, unclesHash, beneficiary, difficulty, number, gasLimit, timestamp, extraData);
            header.MixHash = Keccak.Zero;
            Block block = new(header);
            block.Header.Bloom = Bloom.Empty;
            block.Header.Hash = block.CalculateHash();
            return block;
        }

        [Test]
        public void BlockSealer()
        {
            BlockHeader header = BuildCliqueBlock();

            Address expectedBlockSealer = new("0xb279182d99e65703f0076e4812653aab85fca0f0");
            Address blockSealer = _snapshotManager.GetBlockSealer(header);
            Assert.That(blockSealer, Is.EqualTo(expectedBlockSealer));
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
    }
}
