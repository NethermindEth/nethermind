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
// 

using System;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Trie.Test
{
    [TestFixture]
    public class RustVerkleLibTest
    {
        private readonly byte[] treeKeyVersion =
        {
            121, 85, 7, 198, 131, 230, 143, 90, 165, 129, 173, 81, 186, 89, 19, 191, 13, 107, 197, 120, 243, 229,
            224, 183, 72, 25, 6, 8, 210, 159, 31, 0
        };

        private readonly byte[] treeKeyBalance =
        {
            121, 85, 7, 198, 131, 230, 143, 90, 165, 129, 173, 81, 186, 89, 19, 191, 13, 107, 197, 120, 243, 229,
            224, 183, 72, 25, 6, 8, 210, 159, 31, 1
        };
        
        private readonly byte[] treeKeyNonce =
        {
            121, 85, 7, 198, 131, 230, 143, 90, 165, 129, 173, 81, 186, 89, 19, 191, 13, 107, 197, 120, 243, 229,
            224, 183, 72, 25, 6, 8, 210, 159, 31, 2
        };
        
        private readonly byte[] treeKeyCodeKeccak =
        {
            121, 85, 7, 198, 131, 230, 143, 90, 165, 129, 173, 81, 186, 89, 19, 191, 13, 107, 197, 120, 243, 229,
            224, 183, 72, 25, 6, 8, 210, 159, 31, 3
        };

        private readonly byte[] treeKeyCodeSize =
        {
            121, 85, 7, 198, 131, 230, 143, 90, 165, 129, 173, 81, 186, 89, 19, 191, 13, 107, 197, 120, 243, 229,
            224, 183, 72, 25, 6, 8, 210, 159, 31, 4
        };

        private readonly byte[] emptyCodeHashValue =
        {
            197, 210, 70, 1, 134, 247, 35, 60, 146, 126, 125, 178, 220, 199, 3, 192, 229, 0, 182, 83, 202, 130, 39,
            59, 123, 250, 216, 4, 93, 133, 164, 112
        };
        
        private readonly byte[] value0 =  {
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
        };

        private readonly byte[] value2 =  {
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2
        };

        private readonly byte[] ValueStart2 =  {
            0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
        };


        [Test]
        public void TestInsertGet()
        {
            byte[] one = {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1};
            byte[] one32 = {1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1};

            IntPtr trie = RustVerkleLib.VerkleTrieNew();

            RustVerkleLib.VerkleTrieInsert(trie, one, one32);
            RustVerkleLib.VerkleTrieInsert(trie, one32, one);
            
            byte[] array32 = RustVerkleLib.VerkleTrieGet(trie, one32);
            Assert.True(checkIfEqual(one, array32));

            byte[] array = RustVerkleLib.VerkleTrieGet(trie, one);
            Assert.True(checkIfEqual(one32, array));
        }

        [Test]
        public void TestInsertRawAccountValues()
        {
            IntPtr trie = RustVerkleLib.VerkleTrieNew();
            UInt256 version = UInt256.Zero;
            UInt256 balance = new (2);
            UInt256 nonce = UInt256.Zero;
            Keccak codeHash = Keccak.OfAnEmptyString;
            UInt256 codeSize = UInt256.Zero;
            
            RustVerkleLib.VerkleTrieInsert(trie, treeKeyVersion, version.ToBigEndian());
            RustVerkleLib.VerkleTrieInsert(trie, treeKeyBalance, balance.ToBigEndian());
            RustVerkleLib.VerkleTrieInsert(trie, treeKeyNonce, nonce.ToBigEndian());
            RustVerkleLib.VerkleTrieInsert(trie, treeKeyCodeKeccak, codeHash.Bytes);
            RustVerkleLib.VerkleTrieInsert(trie, treeKeyCodeSize, codeSize.ToBigEndian());
            
            byte[] versionVal = RustVerkleLib.VerkleTrieGet(trie, treeKeyVersion);
            Assert.True(checkIfEqual(version.ToBigEndian(), versionVal));
            byte[] balanceVal = RustVerkleLib.VerkleTrieGet(trie, treeKeyBalance);
            Assert.True(checkIfEqual(balance.ToBigEndian(), balanceVal));
            byte[] nonceVal = RustVerkleLib.VerkleTrieGet(trie, treeKeyNonce);
            Assert.True(checkIfEqual(nonce.ToBigEndian(), nonceVal));
            byte[] codeKeccakVal = RustVerkleLib.VerkleTrieGet(trie, treeKeyCodeKeccak);
            Assert.True(checkIfEqual(codeHash.Bytes, codeKeccakVal));
            byte[] codeSizeVal = RustVerkleLib.VerkleTrieGet(trie, treeKeyCodeSize);
            Assert.True(checkIfEqual(codeSize.ToBigEndian(), codeSizeVal));
            
        }


        [Test]
        public void TestInsertAccount()
        {
            IntPtr trie = RustVerkleLib.VerkleTrieNew();
            RustVerkleLib.VerkleTrieInsert(trie, treeKeyVersion, value0);
            RustVerkleLib.VerkleTrieInsert(trie, treeKeyBalance, value2);
            RustVerkleLib.VerkleTrieInsert(trie, treeKeyNonce, value0);
            RustVerkleLib.VerkleTrieInsert(trie, treeKeyCodeKeccak, emptyCodeHashValue);
            RustVerkleLib.VerkleTrieInsert(trie, treeKeyCodeSize, value0);
            
            byte[] version = RustVerkleLib.VerkleTrieGet(trie, treeKeyVersion);
            Assert.True(checkIfEqual(version, value0));
            byte[] balance = RustVerkleLib.VerkleTrieGet(trie, treeKeyBalance);
            Assert.True(checkIfEqual(balance, value2));
            byte[] nonce = RustVerkleLib.VerkleTrieGet(trie, treeKeyNonce);
            Assert.True(checkIfEqual(nonce, value0));
            byte[] codeKeccak = RustVerkleLib.VerkleTrieGet(trie, treeKeyCodeKeccak);
            Assert.True(checkIfEqual(codeKeccak, emptyCodeHashValue));
            byte[] codeSize = RustVerkleLib.VerkleTrieGet(trie, treeKeyCodeSize);
            Assert.True(checkIfEqual(codeSize, value0));
            
        }
        
        public bool checkIfEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
            {
                return false;
            }

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }
        
        [Test]
        public void TestGetStateRoot()
        {
            byte[] expectedHash =
            {
                126, 78, 128, 195, 158, 198, 161, 181, 168, 62, 72, 164, 253, 156, 158, 75, 153, 239, 132, 63, 159,
                5, 16, 15, 174, 208, 244, 102, 120, 109, 200, 11
            };
            byte[] zero = new byte[32];
            byte[] one = {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1};
            byte[] one32 = {1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1};

            IntPtr trie = RustVerkleLib.VerkleTrieNew();
            byte[] stateRootNew = RustVerkleLib.VerkleTrieGetStateRoot(trie);
            Assert.AreEqual(stateRootNew, zero);
            RustVerkleLib.VerkleTrieInsert(trie, one, one);
            RustVerkleLib.VerkleTrieInsert(trie, one32, one);
            byte[] stateRootAfter = RustVerkleLib.VerkleTrieGetStateRoot(trie);
            Assert.AreEqual(stateRootAfter, expectedHash);
        }
        
        [Test]
        public void TestProofVerify(){
            byte[] one = {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1};
            byte[] one32 = {1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1};

            IntPtr trie = RustVerkleLib.VerkleTrieNew();

            RustVerkleLib.VerkleTrieInsert(trie, one, one32);
            RustVerkleLib.VerkleTrieInsert(trie, one32, one);

            byte[] proof = RustVerkleLib.VerkleProofGet(trie, one32);
            bool verification = RustVerkleLib.VerkleProofVerify(trie, proof, proof.Length, one32, one);
            Assert.IsTrue(verification);
            verification = RustVerkleLib.VerkleProofVerify(trie, proof, proof.Length, one32, one32);
            Assert.IsTrue(!verification);
        }

        [Test]
        public void MultipleValueOperations()
        {
            byte[] one = {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1};
            byte[] one32 = {1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1};

            IntPtr trie = RustVerkleLib.VerkleTrieNew();

            byte[,] keys = new byte[,] {
                                            {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1}, 
                                            {1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1}
                                       };
            byte[,] vals = new byte[,] {
                                            {1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1},
                                            {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1} 
                                       };
            
            RustVerkleLib.VerkleTrieInsertMultiple(trie, keys, vals, keys.GetLength(0)); 

            byte[] array32 = RustVerkleLib.VerkleTrieGet(trie, one32);
            Assert.True(checkIfEqual(one, array32));
            
            byte[] array = RustVerkleLib.VerkleTrieGet(trie, one);
            Assert.True(checkIfEqual(array, one32));

            byte[] proof = RustVerkleLib.VerkleProofGetMultiple(trie, keys, keys.GetLength(0));

            bool verification = RustVerkleLib.VerkleProofVerifyMultiple(trie, proof, proof.Length, keys, vals, keys.GetLength(0));
            Assert.IsTrue(verification);

            // Console.WriteLine("Verifying first value");
            // verification = RustVerkleLibTest.VerifyVerkleProof(trie, proof, )
        }
        
    }
}
