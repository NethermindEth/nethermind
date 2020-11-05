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

using System.Threading.Tasks;
using Nethermind.Baseline.Test.Contracts;
using Nethermind.Baseline.Tree;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Baseline.Test
{
    public partial class BaselineTreeTrackerTests
    {
        [Test]
        public async Task Tree_tracker_reorganization([ValueSource(nameof(InsertLeafTestCases))]InsertLeafTest test)
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

            //testRpc.BlockProducer.BlockParent = testRpc.BlockTree.FindHeader(5);

            //await testRpc.AddBlock(false);
            //testRpc.BlockProducer.BlockParent = testRpc.BlockProducer.LastProducedBlock.Header;
            //await testRpc.AddBlock();
        }
    }
}
