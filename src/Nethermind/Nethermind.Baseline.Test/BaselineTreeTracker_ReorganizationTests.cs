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
            var fromContractAdress = ContractAddress.From(address, 0L);
            var baselineTreeHelper = new BaselineTreeHelper(testRpc.LogFinder, _baselineDb, _metadataBaselineDb);
            new BaselineTreeTracker(fromContractAdress, baselineTree, testRpc.BlockProcessor, baselineTreeHelper, testRpc.BlockFinder);

            var contract = new MerkleTreeSHAContract(_abiEncoder, fromContractAdress);
            UInt256 nonce = 1L;
            for (int i = 0; i < test.ExpectedTreeCounts.Length; i++)
            {
                nonce = await SendTransactions(test.LeavesInTransactionsAndBlocks[i], nonce, testRpc, contract, address);

                await testRpc.AddBlock();
                Assert.AreEqual(test.ExpectedTreeCounts[i], baselineTree.Count);
            }

            var initBlocksCount = 4;
            var allBlocksCount = initBlocksCount + test.ExpectedTreeCounts.Length;
            testRpc.BlockProducer.BlockParent = testRpc.BlockTree.FindHeader(allBlocksCount);

            nonce = 1L;
            nonce = await SendTransactions(test.LeavesInMiddleOfReorganization, nonce, testRpc, contract, address);

            await testRpc.AddBlock(false);
            testRpc.BlockProducer.BlockParent = testRpc.BlockProducer.LastProducedBlock.Header;

            await SendTransactions(test.LeavesInAfterReorganization, nonce, testRpc, contract, address);

            await testRpc.AddBlock();
            Assert.AreEqual(test.TreeCountAfterAll, baselineTree.Count);
        }

        private async Task<UInt256> SendTransactions(Keccak[] transactions, UInt256 startingNonce, TestRpcBlockchain testRpc, MerkleTreeSHAContract contract, Address address)
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

        public class ReorganizedInsertLeafTest
        {
            // first dimensions - blocks, second dimensions - transactions
            public Keccak[][] LeavesInTransactionsAndBlocks { get; set; }
            public int[] ExpectedTreeCounts { get; set; }

            public Keccak[] LeavesInMiddleOfReorganization { get; set; }

            public Keccak[] LeavesInAfterReorganization { get; set; }

            public int TreeCountAfterAll { get; set; }

            public override string ToString() => "Tree counts: " + string.Join("; ", ExpectedTreeCounts.Select(x => x.ToString())) + $", After all count: {TreeCountAfterAll}" ;
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
                    ExpectedTreeCounts = new int[]
                    {
                        1 // tree count after first block
                    },
                    LeavesInMiddleOfReorganization = new Keccak[] { },
                    LeavesInAfterReorganization = new Keccak[] { },
                    TreeCountAfterAll = 1
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
                    ExpectedTreeCounts = new int[]
                    {
                        1 // tree count after first block
                    },
                    LeavesInMiddleOfReorganization = new Keccak[] { TestItem.KeccakD },
                    LeavesInAfterReorganization = new Keccak[] { TestItem.KeccakC },
                    TreeCountAfterAll = 3
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
                    ExpectedTreeCounts = new int[]
                    {
                        1 // tree count after first block
                    },
                    LeavesInMiddleOfReorganization = new Keccak[] { TestItem.KeccakD },
                    LeavesInAfterReorganization = new Keccak[] { TestItem.KeccakC },
                    TreeCountAfterAll = 3
                };
            }
        }
    }
}
