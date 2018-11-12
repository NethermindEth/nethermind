/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Store;
using NUnit.Framework;

namespace Nethermind.Clique.Test
{
    public class SnapshotTests
    {
        private CliqueSealEngine _clique;

        private IDb _snapshotDb = new MemDb();

        private const string Block1Rlp = "f9025bf90256a06341fd3daf94b748c72ced5a5b26028f2474f5f00d824504e4fa37a75767e177a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a053580584816f617295ea26c0e17641e0120cab2f0a8ffb53a866fd53aa8e8c2da056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b901000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000002018347c94c808458ee45dab861d783010600846765746887676f312e372e33856c696e757800000000000000009f1efa1efa72af138c915966c639544a0255e6288e188c22ce9168c10dbe46da3d88b4aa065930119fb886210bf01a084fde5d3bc48d8aa38bca92e4fcc5215100a00000000000000000000000000000000000000000000000000000000000000000880000000000000000c0c0";
        private const string Block2Rlp = "f9025bf90256a0a7684ac44d48494670b2e0d9085b7750e7341620f0a271db146ed5e70c1db854a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a053580584816f617295ea26c0e17641e0120cab2f0a8ffb53a866fd53aa8e8c2da056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b901000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000002028347db3d808458ee45eab861d783010600846765746887676f312e372e33856c696e75780000000000000000b5a4a624d2e19fdab62ff7f4d2f2b80dfab4c518761beb56c2319c4224dd156f698bb1a2750c7edf12d61c4022079622062039637f40fb817e2cce0f0a4dae9c01a00000000000000000000000000000000000000000000000000000000000000000880000000000000000c0c0";
        private const string Block3Rlp = "f9025bf90256a09b095b36c15eaf13044373aef8ee0bd3a382a5abb92e402afa44b8249c3a90e9a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a053580584816f617295ea26c0e17641e0120cab2f0a8ffb53a866fd53aa8e8c2da056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b901000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000002038347e7c4808458ee45f9b861d783010600846765746887676f312e372e33856c696e757800000000000000004e10f96536e45ceca7e34cc1bdda71db3f3bb029eb69afd28b57eb0202c0ec0859d383a99f63503c4df9ab6c1dc63bf6b9db77be952f47d86d2d7b208e77397301a00000000000000000000000000000000000000000000000000000000000000000880000000000000000c0c0";
        private const string Block4Rlp = "f9025bf90256a09eb9db9c3ec72918c7db73ae44e520139e95319c421ed6f9fc11fa8dd0cddc56a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a053580584816f617295ea26c0e17641e0120cab2f0a8ffb53a866fd53aa8e8c2da056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b901000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000002048347e7c4808458ee4608b861d783010600846765746887676f312e372e33856c696e75780000000000000000713c53f21fd59a94de9c3f8342777f6660a3e99187114ebf52f0127caf6bcefa77195308fb80b4e6223673757732485c234d8f431a99c46799c57a4ecc4e4e5401a00000000000000000000000000000000000000000000000000000000000000000880000000000000000c0c0";
        private const string Block5Rlp = "f9025bf90256a08dabb64040467fa4e99a061878d90396978d173ecf47b2f72aa31e8d7ad917a9a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a053580584816f617295ea26c0e17641e0120cab2f0a8ffb53a866fd53aa8e8c2da056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b901000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000002058347e7c4808458ee4617b861d783010600846765746887676f312e372e33856c696e7578000000000000000052ad0baf5fefa05b3a51cdcc6484901465c66be48c3c9b7a4fcb5fcb867ea220390bcb6e4d740bc17d0c9e948cf0803cab107b538fb3a3efde89e26ede9ee26801a00000000000000000000000000000000000000000000000000000000000000000880000000000000000c0c0";

        private readonly Address _signer1 = new Address("0x7ffc57839b00206d1ad20c69a1981b489f772031");
        private readonly Address _signer2 = new Address("0xb279182d99e65703f0076e4812653aab85fca0f0");
        private readonly Address _signer3 = new Address("0x42eb768f2244c8811c63729a21a3569731535f06");
        
        [OneTimeSetUp]
        public void Setup_chain()
        {
            // Import blocks
            BlockTree blockTree = Build.A.BlockTree().TestObject;
            Block block1 = Rlp.Decode<Block>(new Rlp(Bytes.FromHexString(Block1Rlp)));
            Block block2 = Rlp.Decode<Block>(new Rlp(Bytes.FromHexString(Block2Rlp)));
            Block block3 = Rlp.Decode<Block>(new Rlp(Bytes.FromHexString(Block3Rlp)));
            Block block4 = Rlp.Decode<Block>(new Rlp(Bytes.FromHexString(Block4Rlp)));
            Block block5 = Rlp.Decode<Block>(new Rlp(Bytes.FromHexString(Block5Rlp)));
            Block genesisBlock = GetRinkebyGenesis();
            // Add blocks
            MineBlock(blockTree, genesisBlock);
            MineBlock(blockTree, block1);
            MineBlock(blockTree, block2);
            MineBlock(blockTree, block3);
            MineBlock(blockTree, block4);
            MineBlock(blockTree, block5);
            // Get a test private key
            PrivateKey key = Build.A.PrivateKey.TestObject;
            // Init snapshot db
            IDb db = new MemDb();
            CliqueConfig config = GetRinkebyConfig();
            _clique = new CliqueSealEngine(config, NullEthereumSigner.Instance, key, db, blockTree, NullLogManager.Instance);
        }

        [Test]
        public void Creates_new_snapshot()
        {
            CliqueConfig config = GetRinkebyConfig();
            LruCache<Keccak, Address> sigCache = new LruCache<Keccak, Address>(10);
            Block genesis = GetRinkebyGenesis();
            SortedList<Address, UInt256> signers = new SortedList<Address, UInt256>(CliqueAddressComparer.Instance)
            {
                {_signer1, 0},
                {_signer2, 0},
                {_signer3, 0},
            };
            Dictionary<Address, Tally> tally = new Dictionary<Address, Tally>();
            Snapshot snapshot = new Snapshot(
                config, sigCache, genesis.Number, genesis.Hash, signers, tally);
            snapshot.Store(_snapshotDb);
        }

        [Test]
        public void Loads_snapshot()
        {
            CliqueConfig config = GetRinkebyConfig();
            LruCache<Keccak, Address> sigCache = new LruCache<Keccak, Address>(10);
            Block genesis = GetRinkebyGenesis();
            Snapshot snapshot = Snapshot.LoadSnapshot(
                config, sigCache, _snapshotDb, genesis.Hash);
            Assert.NotNull(snapshot);
            Assert.AreEqual(config, snapshot.Config);
            Assert.AreEqual(sigCache, snapshot.SigCache);
            Assert.AreEqual(genesis.Hash, snapshot.Hash);
            Assert.AreEqual(genesis.Number, snapshot.Number);
            // Check signers
            Assert.IsTrue(snapshot.Signers.ContainsKey(_signer1));
            Assert.IsTrue(snapshot.Signers.ContainsKey(_signer2));
            Assert.IsTrue(snapshot.Signers.ContainsKey(_signer3));
        }

        [Test]
        public void Recognises_signer_turn()
        {
            CliqueConfig config = GetRinkebyConfig();
            LruCache<Keccak, Address> sigCache = new LruCache<Keccak, Address>(10);
            Block genesis = GetRinkebyGenesis();
            Snapshot snapshot = Snapshot.LoadSnapshot(config, sigCache, _snapshotDb, genesis.Hash);
            // Block 1
            Assert.IsTrue(snapshot.InTurn(1, _signer1));
            Assert.IsFalse(snapshot.InTurn(1, _signer2));
            Assert.IsFalse(snapshot.InTurn(1, _signer3));
            // Block 2
            Assert.IsFalse(snapshot.InTurn(2, _signer1));
            Assert.IsTrue(snapshot.InTurn(2, _signer2));
            Assert.IsFalse(snapshot.InTurn(2, _signer3));
            // Block 3
            Assert.IsFalse(snapshot.InTurn(3, _signer1));
            Assert.IsFalse(snapshot.InTurn(3, _signer2));
            Assert.IsTrue(snapshot.InTurn(3, _signer3));
        }

        [Test]
        public void Encodes()
        {
            SnapshotDecoder decoder = new SnapshotDecoder();
            // Prepare snapshot
            Keccak hash = new Keccak("0xa33ea6f6c0f1c80a6c7af308a30cb7a7affa4d0d51e6639b739727af0518b50e");
            UInt256 number = new UInt256(3305206);
            Address candidate = new Address("0xbe1085bc3e0812f3df63deced87e29b3bc2db524");
            Snapshot expected = GenerateSnapshot(hash, number, candidate);
            // Encode snapshot
            Rlp rlp = decoder.Encode(expected);
            // Decode snapshot
            Snapshot actual = decoder.Decode(rlp.Bytes.AsRlpContext());
            // Validate fields
            Assert.AreEqual(expected.Number, actual.Number);
            Assert.AreEqual(expected.Hash, actual.Hash);
            Assert.AreEqual(expected.Signers, actual.Signers);
            Assert.AreEqual(expected.Votes.Count, actual.Votes.Count);
            for (int i = 0; i < expected.Votes.Count; i++)
            {
                Assert.AreEqual(expected.Votes[i].Signer, actual.Votes[i].Signer);
                Assert.AreEqual(expected.Votes[i].Block, actual.Votes[i].Block);
                Assert.AreEqual(expected.Votes[i].Address, actual.Votes[i].Address);
                Assert.AreEqual(expected.Votes[i].Authorize, actual.Votes[i].Authorize);
            }
            Assert.AreEqual(expected.Tally.Count, actual.Tally.Count);
            foreach (Address address in expected.Tally.Keys)
            {
                Assert.AreEqual(expected.Tally[address].Votes, actual.Tally[address].Votes);
                Assert.AreEqual(expected.Tally[address].Authorize, actual.Tally[address].Authorize);
            }
        }

        private Block GetRinkebyGenesis()
        {
            Keccak parentHash = Keccak.Zero;
            Keccak ommersHash = Keccak.OfAnEmptySequenceRlp;
            Address beneficiary = Address.Zero;
            UInt256 difficulty = new UInt256(1);
            UInt256 number = new UInt256(0);
            int gasLimit = 4700000;
            UInt256 timestamp = new UInt256(1492009146);
            byte[] extraData = Bytes.FromHexString("52657370656374206d7920617574686f7269746168207e452e436172746d616e42eb768f2244c8811c63729a21a3569731535f067ffc57839b00206d1ad20c69a1981b489f772031b279182d99e65703f0076e4812653aab85fca0f00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000");
            BlockHeader header = new BlockHeader(parentHash, ommersHash, beneficiary, difficulty, number, gasLimit, timestamp, extraData);
            Block genesis = new Block(header, new BlockHeader[0]);
            genesis.Hash = new Keccak("0x6341fd3daf94b748c72ced5a5b26028f2474f5f00d824504e4fa37a75767e177");
            return genesis;
        }

        private CliqueConfig GetRinkebyConfig()
        {
            return new CliqueConfig(15, 30000);
        }

        private void MineBlock(BlockTree tree, Block block)
        {
            tree.SuggestBlock(block);
            tree.MarkAsProcessed(block.Hash);
            tree.MoveToMain(block);
        }

        private Snapshot GenerateSnapshot(Keccak hash, UInt256 number, Address candidate)
        {
            CliqueConfig config = GetRinkebyConfig();
            SortedList<Address, UInt256> signers = new SortedList<Address, UInt256>(CliqueAddressComparer.Instance);
            signers.Add(_signer1, number - 2);
            signers.Add(_signer2, number - 1);
            signers.Add(_signer3, number - 3);
            List<Vote> votes = new List<Vote>();
            votes.Add(new Vote(_signer1, number - 2, candidate, true));
            votes.Add(new Vote(_signer3, number - 3, candidate, true));
            votes.Add(new Vote(_signer3, number - 6, _signer2, false));
            Dictionary<Address, Tally> tally = new Dictionary<Address, Tally>();
            tally[candidate] = new Tally(true);
            tally[candidate].Votes = 2;
            tally[_signer2] = new Tally(false);
            tally[_signer2].Votes = 1;
            Snapshot snapshot = new Snapshot(config, null, number, hash, signers, tally);
            snapshot.Votes = votes;
            return snapshot;
        }
    }
}
