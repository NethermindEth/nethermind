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
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Baseline.Test.Contracts;
using Nethermind.Baseline.Tree;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Baseline.Test
{
    public partial class BaselineTreeTrackerTests
    {
        [Test]
        public async Task Tree_tracker_reorganization([ValueSource(nameof(ReorganizationTestCases))]ReorganizedInsertLeafTest test)
        {
            var address = TestItem.Addresses[0];
            var result = await InitializeTestRpc(address);
            var testRpc = result.TestRpc;
            BaselineTree baselineTree = BuildATree();
            var fromContractAddress = ContractAddress.From(address, 0L);
            var baselineTreeHelper = new BaselineTreeHelper(testRpc.LogFinder, _baselineDb, _metadataBaselineDb, LimboNoErrorLogger.Instance);
            new BaselineTreeTracker(fromContractAddress, baselineTree, testRpc.BlockProcessor, baselineTreeHelper, testRpc.BlockFinder, LimboNoErrorLogger.Instance);

            var contract = new MerkleTreeSHAContract(_abiEncoder, fromContractAddress);
            UInt256 nonce = 1L;
            for (int i = 0; i < test.LeavesInBlocksCounts.Length; i++)
            {
                nonce = await InsertLeafFromArray(test.LeavesInTransactionsAndBlocks[i], nonce, testRpc, contract, address);

                await testRpc.AddBlock();
                Assert.AreEqual(test.LeavesInBlocksCounts[i], baselineTree.Count);
            }

            var initBlocksCount = 4;
            var allBlocksCount = initBlocksCount + test.LeavesInBlocksCounts.Length;
            testRpc.BlockProducer.BlockParent = testRpc.BlockTree.FindHeader(allBlocksCount);

            nonce = 1L;
            nonce = await InsertLeafFromArray(test.LeavesInMiddleOfReorganization, nonce, testRpc, contract, address);

            await testRpc.AddBlock(false);
            testRpc.BlockProducer.BlockParent = testRpc.BlockProducer.LastProducedBlock.Header;

            await InsertLeafFromArray(test.LeavesInAfterReorganization, nonce, testRpc, contract, address);

            await testRpc.AddBlock();
            Assert.AreEqual(test.FinalLeavesCount, baselineTree.Count);
        }

        private async Task<UInt256> InsertLeafFromArray(Keccak[] transactions, UInt256 startingNonce, TestRpcBlockchain testRpc, MerkleTreeSHAContract contract, Address address)
        {
            UInt256 nonce = startingNonce;
            for (int j = 0; j < transactions.Length; j++)
            {
                var leafHash = transactions[j];
                var transaction = contract.InsertLeaf(address, leafHash);
                transaction.Nonce = nonce;
                ++nonce;
                await testRpc.TxSender.SendTransaction(transaction, TxPool.TxHandlingOptions.None);
            }

            return nonce;
        }

        private async Task<UInt256> InsertLeavesFromArray(Keccak[][] transactions, UInt256 startingNonce, TestRpcBlockchain testRpc, MerkleTreeSHAContract contract, Address address)
        {
            UInt256 nonce = startingNonce;
            for (int j = 0; j < transactions.Length; j++)
            {
                var hashes = transactions[j];
                var transaction = contract.InsertLeaves(address, hashes);
                transaction.Nonce = nonce;
                ++nonce;
                await testRpc.TxSender.SendTransaction(transaction, TxPool.TxHandlingOptions.None);
            }

            return nonce;
        }

        public class ReorganizedInsertLeafTest
        {
            // first dimensions - blocks, second dimensions - transactions
            public Keccak[][] LeavesInTransactionsAndBlocks { get; set; }
            public int[] LeavesInBlocksCounts { get; set; }

            public Keccak[] LeavesInMiddleOfReorganization { get; set; }

            public Keccak[] LeavesInAfterReorganization { get; set; }

            public int FinalLeavesCount { get; set; }

            public override string ToString() => "Count of leaves in tree: " + string.Join("; ", LeavesInBlocksCounts.Select(x => x.ToString())) + $", Count of leaves after all count: {FinalLeavesCount}" ;
        }

        public static IEnumerable<ReorganizedInsertLeafTest> ReorganizationTestCases
        {
            get
            {
                yield return new ReorganizedInsertLeafTest()
                {
                    LeavesInTransactionsAndBlocks = new Keccak[][]
                    {
                        new Keccak[] // first block
                        {
                            TestItem.KeccakB // first transaction
                        }
                    },
                    LeavesInBlocksCounts = new int[]
                    {
                        1 // tree count after first block
                    },
                    LeavesInMiddleOfReorganization = new Keccak[] { },
                    LeavesInAfterReorganization = new Keccak[] { },
                    FinalLeavesCount = 0
                };

                yield return new ReorganizedInsertLeafTest()
                {
                    LeavesInTransactionsAndBlocks = new Keccak[][]
                    {
                        new Keccak[] // first block
                        {
                            TestItem.KeccakB // first transaction
                        }
                    },
                    LeavesInBlocksCounts = new int[]
                    {
                        1 // tree count after first block
                    },
                    LeavesInMiddleOfReorganization = new Keccak[] { TestItem.KeccakD },
                    LeavesInAfterReorganization = new Keccak[] { TestItem.KeccakC },
                    FinalLeavesCount = 2
                };


                yield return new ReorganizedInsertLeafTest()
                {
                    LeavesInTransactionsAndBlocks = new Keccak[][]
                    {
                        new Keccak[] // first block
                        {
                            TestItem.KeccakB // first transaction
                        },
                        new Keccak[] // second block
                        {
                            TestItem.KeccakH, // first transaction
                            TestItem.KeccakF // second transaction
                        }
                    },
                    LeavesInBlocksCounts = new int[]
                    {
                        1, 3
                    },
                    LeavesInMiddleOfReorganization = new Keccak[] { TestItem.KeccakD, TestItem.KeccakA }, // one is rejected because of nonce
                    LeavesInAfterReorganization = new Keccak[] { TestItem.KeccakC },
                    FinalLeavesCount = 3
                };
            }
        }
    }
}
