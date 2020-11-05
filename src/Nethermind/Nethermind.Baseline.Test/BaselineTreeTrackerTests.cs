//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Security;
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.Baseline.Test.Contracts;
using Nethermind.Baseline.Tree;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Baseline.Test
{
    public partial class BaselineTreeTrackerTests
    {
        private IFileSystem _fileSystem;
        private AbiEncoder _abiEncoder;
        private IDb _baselineDb;
        private IKeyValueStore _metadataBaselineDb;

        [SetUp]
        public void SetUp()
        {
            _fileSystem = Substitute.For<IFileSystem>();
            const string expectedFilePath = "contracts/MerkleTreeSHA.bin";
            _fileSystem.File.ReadAllLinesAsync(expectedFilePath).Returns(File.ReadAllLines(expectedFilePath));
            _abiEncoder = new AbiEncoder();
            _baselineDb = new MemDb();
            _metadataBaselineDb = new MemDb();
        }



        [Test]
        public async Task Tree_tracker_insert_leaf([ValueSource(nameof(InsertLeafTestCases))]InsertLeafTest test)
        {
            var address = TestItem.Addresses[0];
            var result = await InitializeTestRpc(address);
            var testRpc = result.TestRpc;
            BaselineTree baselineTree = BuildATree();
            var fromContractAdress = ContractAddress.From(address, 0L);
            var baselineTreeHelper = new BaselineTreeHelper(testRpc.LogFinder, _baselineDb, _metadataBaselineDb);
            new BaselineTreeTracker(fromContractAdress, baselineTree, testRpc.BlockProcessor, baselineTreeHelper, testRpc.BlockFinder);

            var contract = new MerkleTreeSHAContract(_abiEncoder, fromContractAdress);
            UInt256 nonce = 1L;
            for (int i = 0; i < test.ExpectedTreeCounts.Length; i++)
            {
                for (int j = 0; j < test.LeavesInTransactionsAndBlocks[i].Length; j++)
                {
                    var leafHash = test.LeavesInTransactionsAndBlocks[i][j];
                    var transaction = contract.InsertLeaf(address, leafHash);
                    transaction.Nonce = nonce;
                    ++nonce;
                    await testRpc.TxSender.SendTransaction(transaction, TxPool.TxHandlingOptions.None);
                }

                await testRpc.AddBlock();
                Assert.AreEqual(test.ExpectedTreeCounts[i], baselineTree.Count);
            }
        }

        [Test]
        public async Task Tree_tracker_insert_leaves([ValueSource(nameof(InsertLeavesTestCases))]InsertLeavesTest test)
        {
            var address = TestItem.Addresses[0];
            var result = await InitializeTestRpc(address);
            var testRpc = result.TestRpc;
            BaselineTree baselineTree = BuildATree();
            var fromContractAdress = ContractAddress.From(address, 0);
            var baselineTreeHelper = new BaselineTreeHelper(testRpc.LogFinder, _baselineDb, _metadataBaselineDb);
            new BaselineTreeTracker(fromContractAdress, baselineTree, testRpc.BlockProcessor, baselineTreeHelper, testRpc.BlockFinder);

            var contract = new MerkleTreeSHAContract(_abiEncoder, fromContractAdress);

            UInt256 nonce = 1L;
            for (int i = 0; i < test.ExpectedTreeCounts.Length; i++)
            {
                for (int j = 0; j < test.LeavesInTransactionsAndBlocks[i].Length; j++)
                {
                    var hashes = test.LeavesInTransactionsAndBlocks[i][j];
                    var transaction = contract.InsertLeaves(address, hashes);
                    transaction.Nonce = nonce;
                    ++nonce;
                    await testRpc.TxSender.SendTransaction(transaction, TxPool.TxHandlingOptions.None);
                }

                await testRpc.AddBlock();
                Assert.AreEqual(test.ExpectedTreeCounts[i], baselineTree.Count);
            }
        }

        private async Task<(TestRpcBlockchain TestRpc, BaselineModule BaselineModule)> InitializeTestRpc(Address address)
        {
            var spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            var blockBuilder = Core.Test.Builders.Build.A.Block.Genesis.WithGasLimit(10000000000);
            var testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
                .WithGenesisBlockBuilder(blockBuilder)
                .Build(spec);
            testRpc.TestWallet.UnlockAccount(address, new SecureString());
            await testRpc.AddFunds(address, 1.Ether());

            BaselineModule baselineModule = new BaselineModule(
                testRpc.TxSender,
                testRpc.StateReader,
                testRpc.LogFinder,
                testRpc.BlockTree,
                new AbiEncoder(),
                _fileSystem,
                _baselineDb,
                _metadataBaselineDb,
                LimboLogs.Instance,
                testRpc.BlockProcessor);
            Keccak txHash = (await baselineModule.baseline_deploy(address, "MerkleTreeSHA")).Data;
            await testRpc.AddBlock();
            return (testRpc, baselineModule);
        }

        private BaselineTree BuildATree(IKeyValueStore keyValueStore = null)
        {
            return new ShaBaselineTree(_baselineDb, _metadataBaselineDb, new byte[] { }, 0);
        }


        public class InsertLeafTest
        {
            // first dimensions - blocks, second dimensions - transactions
            public Keccak[][] LeavesInTransactionsAndBlocks { get; set; }
            public int[] ExpectedTreeCounts { get; set; }

            public override string ToString() => "Tree counts: " + string.Join("; ", ExpectedTreeCounts.Select(x => x.ToString()));
        }

        public class InsertLeavesTest
        {
            // first dimensions - blocks, second dimensions - transactions, third - leaves in one transaction
            public Keccak[][][] LeavesInTransactionsAndBlocks { get; set; }
            public int[] ExpectedTreeCounts { get; set; }

            public override string ToString() => "Tree counts: " + string.Join("; ", ExpectedTreeCounts.Select(x => x.ToString()));
        }


        public static IEnumerable<InsertLeafTest> InsertLeafTestCases
        {
            get
            {
                yield return new InsertLeafTest()
                {
                    LeavesInTransactionsAndBlocks = new Keccak[][]
                    {
                        new Keccak[] // first block
                        {
                            TestItem.KeccakB // first transaction
                        }
                    },
                    ExpectedTreeCounts = new int[]
                    {
                        1 // tree count after first block
                    }
                };

                yield return new InsertLeafTest()
                {
                    LeavesInTransactionsAndBlocks = new Keccak[][]
                    {
                        new Keccak[] // first block
                        {
                            TestItem.KeccakB, // first transaction
                            TestItem.KeccakC // second transaction 
                        }
                    },
                    ExpectedTreeCounts = new int[]
                    {
                        2 // tree count after first block
                    }
                };

                yield return new InsertLeafTest()
                {
                    LeavesInTransactionsAndBlocks = new Keccak[][]
                    {
                        new Keccak[] // first block
                        {
                            TestItem.KeccakA, // first transaction
                            TestItem.KeccakC, // second transaction 
                            TestItem.KeccakD, // third transaction
                        },
                        new Keccak[] // second block
                        {
                            TestItem.KeccakB, // first transaction
                            TestItem.KeccakF // second transaction,
                        }
                    },
                    ExpectedTreeCounts = new int[]
                    {
                        3, // tree count after first block
                        5 // tree count after second block
                    }
                };
            }
        }

        public static IEnumerable<InsertLeavesTest> InsertLeavesTestCases
        {
            get
            {
                yield return new InsertLeavesTest()
                {
                    LeavesInTransactionsAndBlocks = new Keccak[][][]
                    {
                        new Keccak[][] // first block
                        {
                            new Keccak[] // first transaction
                            {
                                TestItem.KeccakB
                            }
                        }
                    },
                    ExpectedTreeCounts = new int[]
                    {
                        1 // tree count after first block
                    }
                };

                yield return new InsertLeavesTest()
                {
                    LeavesInTransactionsAndBlocks = new Keccak[][][]
                    {
                        new Keccak[][] // first block
                        {
                            new Keccak[] // first transaction
                            {
                                TestItem.KeccakB, TestItem.KeccakA, TestItem.KeccakC
                            }
                        }
                    },
                    ExpectedTreeCounts = new int[]
                    {
                        3 // tree count after first block
                    }
                };

                yield return new InsertLeavesTest()
                {
                    LeavesInTransactionsAndBlocks = new Keccak[][][]
                    {
                        new Keccak[][] // first block
                        {
                            new Keccak[] // first transaction
                            {
                                TestItem.KeccakB, TestItem.KeccakA, TestItem.KeccakC
                            },
                            new Keccak[] // second transaction
                            {
                                TestItem.KeccakF, TestItem.KeccakD
                            }
                        }
                    },
                    ExpectedTreeCounts = new int[]
                    {
                        5 // tree count after first block
                    }
                };

                yield return new InsertLeavesTest()
                {
                    LeavesInTransactionsAndBlocks = new Keccak[][][]
                    {
                        new Keccak[][] // first block
                        {
                            new Keccak[] // first transaction
                            {
                                TestItem.KeccakB, TestItem.KeccakA, TestItem.KeccakC
                            },
                            new Keccak[] // second transaction
                            {
                                TestItem.KeccakF, TestItem.KeccakD
                            }
                        },
                        new Keccak[][] // second block
                        {
                            new Keccak[] // first transaction
                            {
                                TestItem.KeccakG
                            },
                        }
                    },
                    ExpectedTreeCounts = new int[]
                    {
                        5, 6 // tree count after first block
                    }
                };

                yield return new InsertLeavesTest()
                {
                    LeavesInTransactionsAndBlocks = new Keccak[][][]
                    {
                        new Keccak[][] // first block
                        {
                            new Keccak[] // first transaction
                            {
                                TestItem.KeccakC
                            },
                            new Keccak[] // second transaction
                            {
                                TestItem.KeccakF, TestItem.KeccakD
                            }
                        },
                        new Keccak[][] // second block
                        {
                            new Keccak[] // first transaction
                            {
                                TestItem.KeccakG
                            },
                        },
                        new Keccak[][] // third block
                        {
                            new Keccak[] // first transaction
                            {
                                TestItem.KeccakH
                            },
                            new Keccak[] // first transaction
                            {
                                TestItem.KeccakA, TestItem.KeccakB
                            },
                        }
                    },
                    ExpectedTreeCounts = new int[]
                    {
                        3, 4, 7 // tree count after first block
                    }
                };
            }
        }
    }
}
