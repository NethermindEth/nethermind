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
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using NUnit.Framework;
using System.IO;
using Org.BouncyCastle.Crypto.Engines;

namespace Nethermind.Trie.Test
{
    [TestFixture]
    public class RustVerkleLibTest
    {
        static object[] Variants =
        {
            new object[] {DatabaseScheme.MemoryDb, CommitScheme.TestCommitment, true},
            new object[] {DatabaseScheme.MemoryDb, CommitScheme.TestCommitment, false},
            new object[] {DatabaseScheme.MemoryDb, CommitScheme.PrecomputeLagrange, true},
            new object[] {DatabaseScheme.MemoryDb, CommitScheme.PrecomputeLagrange, false},
            new object[] {DatabaseScheme.RocksDb, CommitScheme.TestCommitment, true},
            new object[] {DatabaseScheme.RocksDb, CommitScheme.TestCommitment, false},
            new object[] {DatabaseScheme.RocksDb, CommitScheme.PrecomputeLagrange, true},
            new object[] {DatabaseScheme.RocksDb, CommitScheme.PrecomputeLagrange, false}
        };
        
        static object[] VariantsRo =
        {
            new object[] {DatabaseScheme.MemoryDb, CommitScheme.TestCommitment},
            new object[] {DatabaseScheme.MemoryDb, CommitScheme.PrecomputeLagrange},
            new object[] {DatabaseScheme.RocksDb, CommitScheme.TestCommitment},
            new object[] {DatabaseScheme.RocksDb, CommitScheme.PrecomputeLagrange},
        };

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

        [TearDown]
        public void CleanTestData()
        {
            string tempDir = Path.GetTempPath();
            string dbname = "VerkleTrie_TestID_" + TestContext.CurrentContext.Test.ID;
            string pathname = Path.Combine(tempDir, dbname);
            if (Directory.Exists(pathname))
            {
                Directory.Delete(pathname,true);
            }
        }
        
        private RustVerkle _verkleTrieNew(DatabaseScheme databaseScheme, CommitScheme commitScheme)
        {
            string tempDir = Path.GetTempPath();
            string dbname = "VerkleTrie_TestID_" + TestContext.CurrentContext.Test.ID;
            string pathname = Path.Combine(tempDir, dbname);
            return RustVerkleLib.VerkleTrieNew(databaseScheme, commitScheme, pathname);
        }
        
        private RustVerkle _verkleTrieNewFromDb(DatabaseScheme databaseScheme, CommitScheme commitScheme)
        {
            string tempDir = Path.GetTempPath();
            string dbname = "VerkleTrie_TestID_" + TestContext.CurrentContext.Test.ID;
            string pathname = Path.Combine(tempDir, dbname);
            RustVerkleDb db = RustVerkleLib.VerkleDbNew(databaseScheme, pathname);
            return RustVerkleLib.VerkleTrieNewFromDb(db, commitScheme);
        }

        [TestCaseSource(nameof(Variants))]
        public void TestInsertByteGet(DatabaseScheme databaseScheme, CommitScheme commitScheme, bool fromDb)
        {
            byte[] one = 
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1
            };
            byte[] one32 = 
            {
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1
            };

            RustVerkle trie = fromDb? _verkleTrieNewFromDb(databaseScheme, commitScheme) : 
                _verkleTrieNew(databaseScheme, commitScheme);

            RustVerkleLib.VerkleTrieInsert(trie, one, one32);
            RustVerkleLib.VerkleTrieInsert(trie, one32, one);

            RustVerkleLib.VerkleTrieGet(trie, one32).Should().BeEquivalentTo(one);
            RustVerkleLib.VerkleTrieGet(trie, one).Should().BeEquivalentTo(one32);
        }
        
        [TestCaseSource(nameof(Variants))]
        public void TestInsertSpanGetSpan(DatabaseScheme databaseScheme, CommitScheme commitScheme, bool fromDb)
        {
            Span<byte> one = new byte[]{0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1};
            Span<byte> one32 = new byte[]{1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1};

            RustVerkle trie = fromDb? _verkleTrieNewFromDb(databaseScheme, commitScheme) : 
                _verkleTrieNew(databaseScheme, commitScheme);

            RustVerkleLib.VerkleTrieInsert(trie, one, one32);
            RustVerkleLib.VerkleTrieInsert(trie, one32, one);

            RustVerkleLib.VerkleTrieGetSpan(trie, one32).Should().BeEquivalentTo(one);
            RustVerkleLib.VerkleTrieGetSpan(trie, one).Should().BeEquivalentTo(one32);
        }

        [TestCaseSource(nameof(Variants))]
        public void TestInsertStackAllocGet(DatabaseScheme databaseScheme, CommitScheme commitScheme, bool fromDb)
        {
            Span<byte> one = stackalloc byte[]
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1
            };
            Span<byte> one32 = stackalloc byte[]
            {
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1
            };

            RustVerkle trie = fromDb? _verkleTrieNewFromDb(databaseScheme, commitScheme) : 
                _verkleTrieNew(databaseScheme, commitScheme);

            RustVerkleLib.VerkleTrieInsert(trie, one, one32);
            RustVerkleLib.VerkleTrieInsert(trie, one32, one);

            RustVerkleLib.VerkleTrieGet(trie, one32).Should().BeEquivalentTo(one);
            RustVerkleLib.VerkleTrieGet(trie, one).Should().BeEquivalentTo(one32);
        }

        [TestCaseSource(nameof(Variants))]
        public void TestInsertRawAccountValues(DatabaseScheme databaseScheme, CommitScheme commitScheme, bool fromDb)
        {
            RustVerkle trie = fromDb? _verkleTrieNewFromDb(databaseScheme, commitScheme) : 
                _verkleTrieNew(databaseScheme, commitScheme);
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
            
            RustVerkleLib.VerkleTrieGet(trie, treeKeyVersion).Should().BeEquivalentTo(version.ToBigEndian());
            RustVerkleLib.VerkleTrieGet(trie, treeKeyBalance).Should().BeEquivalentTo(balance.ToBigEndian());
            RustVerkleLib.VerkleTrieGet(trie, treeKeyNonce).Should().BeEquivalentTo(nonce.ToBigEndian());
            RustVerkleLib.VerkleTrieGet(trie, treeKeyCodeKeccak).Should().BeEquivalentTo(codeHash.Bytes);
            RustVerkleLib.VerkleTrieGet(trie, treeKeyCodeSize).Should().BeEquivalentTo(codeSize.ToBigEndian());
            
        }
        
        [TestCaseSource(nameof(Variants))]
        public void TestInsertAccount(DatabaseScheme databaseScheme, CommitScheme commitScheme, bool fromDb)
        {
            RustVerkle trie = fromDb? _verkleTrieNewFromDb(databaseScheme, commitScheme) : 
                _verkleTrieNew(databaseScheme, commitScheme);
            RustVerkleLib.VerkleTrieInsert(trie, treeKeyVersion, value0);
            RustVerkleLib.VerkleTrieInsert(trie, treeKeyBalance, value2);
            RustVerkleLib.VerkleTrieInsert(trie, treeKeyNonce, value0);
            RustVerkleLib.VerkleTrieInsert(trie, treeKeyCodeKeccak, emptyCodeHashValue);
            RustVerkleLib.VerkleTrieInsert(trie, treeKeyCodeSize, value0);
            
            RustVerkleLib.VerkleTrieGet(trie, treeKeyVersion).Should().BeEquivalentTo(value0);
            RustVerkleLib.VerkleTrieGet(trie, treeKeyBalance).Should().BeEquivalentTo(value2);
            RustVerkleLib.VerkleTrieGet(trie, treeKeyNonce).Should().BeEquivalentTo(value0);
            RustVerkleLib.VerkleTrieGet(trie, treeKeyCodeKeccak).Should().BeEquivalentTo(emptyCodeHashValue);
            RustVerkleLib.VerkleTrieGet(trie, treeKeyCodeSize).Should().BeEquivalentTo(value0);

        }
        
        [TestCaseSource(nameof(Variants))]
        public void TestInsertAccountFlushClear(DatabaseScheme databaseScheme, CommitScheme commitScheme, bool fromDb)
        {
            RustVerkle trie = fromDb? _verkleTrieNewFromDb(databaseScheme, commitScheme) : 
                _verkleTrieNew(databaseScheme, commitScheme);
            RustVerkleLib.VerkleTrieInsert(trie, treeKeyVersion, value0);
            RustVerkleLib.VerkleTrieInsert(trie, treeKeyBalance, value2);
            RustVerkleLib.VerkleTrieInsert(trie, treeKeyNonce, value0);
            RustVerkleLib.VerkleTrieInsert(trie, treeKeyCodeKeccak, emptyCodeHashValue);
            RustVerkleLib.VerkleTrieInsert(trie, treeKeyCodeSize, value0);
            
            RustVerkleLib.VerkleTrieGet(trie, treeKeyVersion).Should().BeEquivalentTo(value0);
            RustVerkleLib.VerkleTrieGet(trie, treeKeyBalance).Should().BeEquivalentTo(value2);
            RustVerkleLib.VerkleTrieGet(trie, treeKeyNonce).Should().BeEquivalentTo(value0);
            RustVerkleLib.VerkleTrieGet(trie, treeKeyCodeKeccak).Should().BeEquivalentTo(emptyCodeHashValue);
            RustVerkleLib.VerkleTrieGet(trie, treeKeyCodeSize).Should().BeEquivalentTo(value0);

            RustVerkleLib.VerkleTrieFlush(trie);
            RustVerkleLib.VerkleTrieInsert(trie, treeKeyVersion, value2);
            RustVerkleLib.VerkleTrieGet(trie, treeKeyVersion).Should().BeEquivalentTo(value2);
            RustVerkleLib.VerkleTrieClear(trie);
            RustVerkleLib.VerkleTrieGet(trie, treeKeyVersion).Should().BeEquivalentTo(value0);
        }
        
        [TestCaseSource(nameof(Variants))]
        public void TestInsertAccountFlushClearStateRoot(DatabaseScheme databaseScheme, CommitScheme commitScheme, bool fromDb)
        {
            RustVerkle trie = fromDb? _verkleTrieNewFromDb(databaseScheme, commitScheme) : 
                _verkleTrieNew(databaseScheme, commitScheme);
            RustVerkleLib.VerkleTrieInsert(trie, treeKeyVersion, value0);
            RustVerkleLib.VerkleTrieInsert(trie, treeKeyBalance, value2);
            RustVerkleLib.VerkleTrieInsert(trie, treeKeyNonce, value0);
            RustVerkleLib.VerkleTrieInsert(trie, treeKeyCodeKeccak, emptyCodeHashValue);
            RustVerkleLib.VerkleTrieInsert(trie, treeKeyCodeSize, value0);
            
            RustVerkleLib.VerkleTrieGet(trie, treeKeyVersion).Should().BeEquivalentTo(value0);
            RustVerkleLib.VerkleTrieGet(trie, treeKeyBalance).Should().BeEquivalentTo(value2);
            RustVerkleLib.VerkleTrieGet(trie, treeKeyNonce).Should().BeEquivalentTo(value0);
            RustVerkleLib.VerkleTrieGet(trie, treeKeyCodeKeccak).Should().BeEquivalentTo(emptyCodeHashValue);
            RustVerkleLib.VerkleTrieGet(trie, treeKeyCodeSize).Should().BeEquivalentTo(value0);

            RustVerkleLib.VerkleTrieFlush(trie);
            byte[] stateRoot1 = RustVerkleLib.VerkleTrieGetStateRoot(trie);
            RustVerkleLib.VerkleTrieInsert(trie, treeKeyVersion, value2);
            byte[] stateRoot2 = RustVerkleLib.VerkleTrieGetStateRoot(trie);
            stateRoot2.Should().NotBeEquivalentTo(stateRoot1);
            RustVerkleLib.VerkleTrieGet(trie, treeKeyVersion).Should().BeEquivalentTo(value2);
            RustVerkleLib.VerkleTrieClear(trie);
            byte[] stateRoot3 = RustVerkleLib.VerkleTrieGetStateRoot(trie);
            RustVerkleLib.VerkleTrieGet(trie, treeKeyVersion).Should().BeEquivalentTo(value0);
            stateRoot3.Should().BeEquivalentTo(stateRoot1);
        }

        [TestCaseSource(nameof(Variants))]
        public void TestGetStateRoot(DatabaseScheme databaseScheme, CommitScheme commitScheme, bool fromDb)
        {
            byte[] expectedHash =
            {
                126, 78, 128, 195, 158, 198, 161, 181, 168, 62, 72, 164, 253, 156, 158, 75, 153, 239, 132, 63, 159,
                5, 16, 15, 174, 208, 244, 102, 120, 109, 200, 11
            };
            byte[] zero = new byte[32];
            byte[] one = {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1};
            byte[] one32 = {1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1};

            RustVerkle trie = fromDb? _verkleTrieNewFromDb(databaseScheme, commitScheme) : 
                _verkleTrieNew(databaseScheme, commitScheme);
            RustVerkleLib.VerkleTrieGetStateRoot(trie).Should().BeEquivalentTo(zero);
            
            RustVerkleLib.VerkleTrieInsert(trie, one, one);
            RustVerkleLib.VerkleTrieInsert(trie, one32, one);
            RustVerkleLib.VerkleTrieGetStateRoot(trie).Should().BeEquivalentTo(expectedHash);
        }
        
        [TestCaseSource(nameof(Variants))]
        public void TestProofVerify(DatabaseScheme databaseScheme, CommitScheme commitScheme, bool fromDb)
        {
            byte[] one = {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1};
            byte[] one32 = {1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1};

            RustVerkle trie = fromDb? _verkleTrieNewFromDb(databaseScheme, commitScheme) : 
                _verkleTrieNew(databaseScheme, commitScheme);
            RustVerkleLib.VerkleTrieInsert(trie, one, one32);
            RustVerkleLib.VerkleTrieInsert(trie, one32, one);

            byte[] proof = RustVerkleLib.VerkleProofGet(trie, one32);
            RustVerkleLib.VerkleProofVerify(trie, proof, proof.Length, one32, one).Should().BeTrue();
            RustVerkleLib.VerkleProofVerify(trie, proof, proof.Length, one32, one32).Should().BeFalse();
        }

        [TestCaseSource(nameof(Variants))]
        public void MultipleValueOperations(DatabaseScheme databaseScheme, CommitScheme commitScheme, bool fromDb)
        {
            byte[] one = {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1};
            byte[] one32 = {1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1};

            RustVerkle trie = fromDb? _verkleTrieNewFromDb(databaseScheme, commitScheme) : 
                _verkleTrieNew(databaseScheme, commitScheme);

            byte[,] keys = {
                {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1}, 
                {1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1}
            };
            
            byte[,] vals = {
                {1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1},
                {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1} 
            };
            
            RustVerkleLib.VerkleTrieInsertMultiple(trie, keys, vals, keys.GetLength(0)); 

            RustVerkleLib.VerkleTrieGet(trie, one32).Should().BeEquivalentTo(one);
            RustVerkleLib.VerkleTrieGet(trie, one).Should().BeEquivalentTo(one32);

            byte[] proof = RustVerkleLib.VerkleProofGetMultiple(trie, keys, keys.GetLength(0));

            RustVerkleLib.VerkleProofVerifyMultiple(
                trie, proof, proof.Length, keys, vals, keys.GetLength(0)).Should().BeTrue();
        }
        
        [TestCaseSource(nameof(VariantsRo))]
        public void TestReadOnlyDb(DatabaseScheme databaseScheme, CommitScheme commitScheme)
        {
            string tempDir = Path.GetTempPath();
            string dbname = "VerkleTrie_TestID_" + TestContext.CurrentContext.Test.ID;
            string pathname = Path.Combine(tempDir, dbname);
            RustVerkleDb db = RustVerkleLib.VerkleDbNew(databaseScheme, pathname);
            RustVerkle trie = RustVerkleLib.VerkleTrieNewFromDb(db, commitScheme);
            
            RustVerkleLib.VerkleTrieInsert(trie, treeKeyVersion, value0);
            RustVerkleLib.VerkleTrieInsert(trie, treeKeyBalance, value2);
            RustVerkleLib.VerkleTrieInsert(trie, treeKeyNonce, value0);
            RustVerkleLib.VerkleTrieInsert(trie, treeKeyCodeKeccak, emptyCodeHashValue);
            RustVerkleLib.VerkleTrieInsert(trie, treeKeyCodeSize, value0);
            
            RustVerkleLib.VerkleTrieGet(trie, treeKeyVersion).Should().BeEquivalentTo(value0);
            RustVerkleLib.VerkleTrieGet(trie, treeKeyBalance).Should().BeEquivalentTo(value2);
            RustVerkleLib.VerkleTrieGet(trie, treeKeyNonce).Should().BeEquivalentTo(value0);
            RustVerkleLib.VerkleTrieGet(trie, treeKeyCodeKeccak).Should().BeEquivalentTo(emptyCodeHashValue);
            RustVerkleLib.VerkleTrieGet(trie, treeKeyCodeSize).Should().BeEquivalentTo(value0);

            RustVerkleLib.VerkleTrieFlush(trie);
            byte[] stateRoot1 = RustVerkleLib.VerkleTrieGetStateRoot(trie);

            RustVerkleDb roDb = RustVerkleLib.VerkleTrieGetReadOnlyDb(db);
            RustVerkle roTrie = RustVerkleLib.VerkleTrieNewFromDb(roDb, commitScheme);
            
            RustVerkleLib.VerkleTrieInsert(roTrie, treeKeyVersion, value2);
            byte[] stateRoot2 = RustVerkleLib.VerkleTrieGetStateRoot(roTrie);
            stateRoot2.Should().NotBeEquivalentTo(stateRoot1);
            RustVerkleLib.VerkleTrieGet(roTrie, treeKeyVersion).Should().BeEquivalentTo(value2);
            RustVerkleLib.VerkleTrieFlush(roTrie);
            RustVerkleLib.VerkleTrieClear(roTrie);
            RustVerkleLib.VerkleTrieClearTempChanges(roDb);
            byte[] stateRoot3 = RustVerkleLib.VerkleTrieGetStateRoot(roTrie);
            RustVerkleLib.VerkleTrieGet(roTrie, treeKeyVersion).Should().BeEquivalentTo(value0);
            stateRoot3.Should().BeEquivalentTo(stateRoot1);
        }
        
    }
}
