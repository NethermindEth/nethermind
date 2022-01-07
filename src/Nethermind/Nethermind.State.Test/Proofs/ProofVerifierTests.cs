//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

#nullable enable 

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Store.Test.Proofs
{
    [TestFixture]
    public class ProofVerifierTests
    {
        private readonly Account _account0 = Build.An.Account.WithBalance(0).TestObject;
        private readonly Account _account1 = Build.An.Account.WithBalance(1).TestObject;


        [Test]
        public void VerifyOneAccount()
        {
            MemDb db = new MemDb();
            TrieStore? store = new TrieStore(db, LimboLogs.Instance);
            StateTree tree = new StateTree(store, LimboLogs.Instance);
            tree.Set(new Keccak("0000000000000000000000000000000000000000000000000000000001000001"), _account0);
            tree.Set(new Keccak("0000000000000000000000000000000000000000000000000000000001000002"), _account1);

            tree.Commit(0);

            AccountDecoder accountDecoder = new AccountDecoder();
            string? ext = "0xf842a01000000000000000000000000000000000000000000000000000000000100000a05b3c471437cf4bfcd450811e91de4a0d00a82a05bde6285b655a817651bd2145";
            string branch = "0xf85180a05911f24d96912350de50f297c2d34d5d10e136757bf4cfff5fa41bfca219554aa07cb2e18773f0d6e68a3fa7510be38f089f9cdaeb04d37d801e854b3fd47748fe8080808080808080808080808080";
            string acc01 = "0xf84920b846f8448080a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a0c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470";

            byte[][] proofs = new byte[][] { Bytes.FromHexString(ext), Bytes.FromHexString(branch), Bytes.FromHexString(acc01)};

            var resOne = ProofVerifier.VerifyOneProof(proofs, tree.RootHash);
            var resMultiple = ProofVerifier.VerifyMultipleProofs(proofs, tree.RootHash);

            Assert.IsTrue(resMultiple.provedToBeCorrect);
            Assert.AreEqual(1, resMultiple.provedValues.Count);
            Assert.NotNull(resMultiple.provedValues[0]);
            Assert.IsTrue(acc01.Contains(Bytes.ToHexString(resMultiple.provedValues[0]!)));
        }

        [Test]
        public void VerifyTwoAccounts()
        {
            MemDb db = new MemDb();
            TrieStore? store = new TrieStore(db, LimboLogs.Instance);
            StateTree tree = new StateTree(store, LimboLogs.Instance);
            tree.Set(new Keccak("0000000000000000000000000000000000000000000000000000000001000001"), _account0);
            tree.Set(new Keccak("0000000000000000000000000000000000000000000000000000000001000002"), _account1);

            tree.Commit(0);

            AccountDecoder accountDecoder = new AccountDecoder();
            string? ext = "0xf842a01000000000000000000000000000000000000000000000000000000000100000a05b3c471437cf4bfcd450811e91de4a0d00a82a05bde6285b655a817651bd2145";
            string branch = "0xf85180a05911f24d96912350de50f297c2d34d5d10e136757bf4cfff5fa41bfca219554aa07cb2e18773f0d6e68a3fa7510be38f089f9cdaeb04d37d801e854b3fd47748fe8080808080808080808080808080";
            string acc01 = "0xf84920b846f8448080a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a0c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470";
            string acc02 = "0xf84920b846f8448001a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a0c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470";

            byte[][] proofs = new byte[][] {
                Bytes.FromHexString(ext), Bytes.FromHexString(branch), Bytes.FromHexString(acc01),
                Bytes.FromHexString(ext), Bytes.FromHexString(branch), Bytes.FromHexString(acc02) };

            var result = ProofVerifier.VerifyMultipleProofs(proofs, tree.RootHash);

            Assert.IsTrue(result.provedToBeCorrect);
            Assert.AreEqual(2, result.provedValues.Count);
            Assert.NotNull(result.provedValues[0]);
            Assert.NotNull(result.provedValues[1]);
            Assert.IsTrue(acc02.Contains(Bytes.ToHexString(result.provedValues[0]!)));
            Assert.IsTrue(acc01.Contains(Bytes.ToHexString(result.provedValues[1]!)));
        }


        [Test]
        public void FromKarim()
        {
            MemDb db = new MemDb();
            TrieStore? store = new TrieStore(db, LimboLogs.Instance);
            
            StateTree tree = new StateTree(store, LimboLogs.Instance);

            AccountDecoder accountDecoder = new AccountDecoder(true);

            string branch = "0xf8f1a0668a54c4a82e8830ac43e2753ce7f8ae5330bdef5eea54e63316a4d7479ed078a0c5eb07f6128d0a8520e072e18d2bbeddb471fbd4d5fe85c7de378ba87ba8ac10808080a006eb8770cc0678269b16dde0c2fa3112a3861f624b121a3a96ac36b29205edec808080a0c65d37ec90d7b7d74ea736285b535a8c2123cd0d8b0c1dc70c1ec7d90423ee3f80a0a28f1e7040e0c081c3c7896eba90469ab7fea432ef51fe7041c0e4a97a771eda80a02579c4c22a19872ddf077b34446366913f783a5dc4657b14d1024a145af7c23ea046a729895a4cb6d718b79af220981dc7cbb4018265bbd89f5950a31d472d37498080";
            string leaf = "0xf873a03db15b18b2c004bb8dae65a6875cddf207de4e97a3d34ef71cd7f6cc7fbb94eab850f84e808a130ee8e7179044400000a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a0c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470";

            byte[][] proofs = new byte[][] { Bytes.FromHexString(branch), Bytes.FromHexString(leaf)};

            var result = ProofVerifier.VerifyMultipleProofs(proofs, new Keccak("0xf1f80304eb6f3576de32bd43713fd7ed034783644a27900de46106c016370112"));

            Assert.IsTrue(result.provedToBeCorrect);
            Assert.AreEqual(1, result.provedValues.Count);
            Assert.NotNull(result.provedValues[0]);

            var account = accountDecoder.Decode(new RlpStream(result.provedValues[0]!));
            Assert.AreEqual(new UInt256(0), account!.Nonce);
            Assert.AreEqual(new UInt256(16782408444807217152, 4878, 0, 0), account!.Balance);
            Assert.AreEqual(Keccak.EmptyTreeHash, account!.StorageRoot);
            Assert.AreEqual(Keccak.OfAnEmptyString, account!.CodeHash);
        }
    }
}
