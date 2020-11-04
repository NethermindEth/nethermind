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
        [Test]
        public async Task Tree_tracker_insert_leaf2([ValueSource(nameof(InsertLeafTestCases))]InsertLeafTest test)
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

            testRpc.BlockProducer.BlockParent = testRpc.BlockTree.FindHeader(5);

            await testRpc.AddBlock(false);
            await Task.Delay(1000);
            testRpc.BlockProducer.BlockParent = testRpc.BlockProducer.LastProducedBlock.Header;
            await testRpc.AddBlock();
        }
    }
}
