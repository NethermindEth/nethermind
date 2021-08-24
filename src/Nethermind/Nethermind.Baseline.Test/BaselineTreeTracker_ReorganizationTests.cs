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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Baseline.Test.Contracts;
using Nethermind.Baseline.Tree;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Baseline.Test
{
    public partial class BaselineTreeTrackerTests
    {
        [Test]
        [Ignore("Failing after changing how block are produced.")]
        public async Task Tree_tracker_reorganization([ValueSource(nameof(ReorganizationTestCases))]ReorganizedInsertLeafTest test)
        {
            Address address = TestItem.Addresses[0];
            (TestRpcBlockchain TestRpc, BaselineModule BaselineModule) result = await InitializeTestRpc(address);
            TestRpcBlockchain testRpc = result.TestRpc;
            BaselineTree baselineTree = BuildATree();
            Address contractAddress = ContractAddress.From(address, 0L);
            BaselineTreeHelper baselineTreeHelper = new (testRpc.LogFinder, _baselineDb, _metadataBaselineDb, LimboNoErrorLogger.Instance);
            _ = new BaselineTreeTracker(contractAddress, baselineTree, testRpc.BlockProcessor, baselineTreeHelper, testRpc.BlockFinder, LimboNoErrorLogger.Instance);

            MerkleTreeSHAContract contract = new (_abiEncoder, contractAddress);
            for (int i = 0; i < test.LeavesInBlocksCounts.Length; i++)
            {
                InsertLeafFromArray(test.LeavesInTransactionsAndBlocks[i], testRpc, contract, address);

                await testRpc.AddBlock();
                Assert.AreEqual(test.LeavesInBlocksCounts[i], baselineTree.Count);
            }

            int initBlocksCount = 4;
            int allBlocksCount = initBlocksCount + test.LeavesInBlocksCounts.Length;
            TestBlockProducer testRpcBlockProducer = (TestBlockProducer) testRpc.BlockProducer;
            Block lastProducedBlock = null;
            testRpcBlockProducer.BlockProduced += (o, e) => lastProducedBlock = e.Block;
            testRpcBlockProducer.BlockParent = testRpc.BlockTree.FindHeader(allBlocksCount);

            InsertLeafFromArray(test.LeavesInMiddleOfReorganization, testRpc, contract, address);

            await testRpc.AddBlock(false);
            testRpcBlockProducer.BlockParent = lastProducedBlock.Header;

            InsertLeafFromArray(test.LeavesInAfterReorganization, testRpc, contract, address);

            await testRpc.AddBlock();
            Assert.AreEqual(test.FinalLeavesCount, baselineTree.Count);
        }

        private static readonly CryptoRandom _cryptoRandom = new ();

        private void InsertLeafFromArray(Keccak[] transactions, TestRpcBlockchain testRpc,
            MerkleTreeSHAContract contract, Address address)
        {
            PrivateKeyGenerator generator = new (_cryptoRandom);
            EthereumEcdsa ecdsa = new (testRpc.BlockTree.ChainId, testRpc.LogManager);
            for (int j = 0; j < transactions.Length; j++)
            {
                Keccak leafHash = transactions[j];
                Transaction transaction = contract.InsertLeaf(address, leafHash);
                PrivateKey key = generator.Generate();
                transaction.SenderAddress = key.Address;
                ecdsa.Sign(key, transaction, true);
                transaction.Hash = transaction.CalculateHash();
                AddTxResult result = testRpc.TxPool.SubmitTx(transaction, TxHandlingOptions.None);
                if (result != AddTxResult.Added)
                {
                    throw new Exception("failed to add " + result);
                }
            }
        }

        private void InsertLeavesFromArray(Keccak[][] transactions, TestRpcBlockchain testRpc,
            MerkleTreeSHAContract contract, Address address)
        {
            PrivateKeyGenerator generator = new (_cryptoRandom);
            EthereumEcdsa ecdsa = new (testRpc.BlockTree.ChainId, testRpc.LogManager);
            for (int j = 0; j < transactions.Length; j++)
            {
                Keccak[] hashes = transactions[j];
                Transaction transaction = contract.InsertLeaves(address, hashes);
                PrivateKey key = generator.Generate();
                transaction.SenderAddress = key.Address;
                ecdsa.Sign(key, transaction, true);
                transaction.Hash = transaction.CalculateHash();
                AddTxResult result = testRpc.TxPool.SubmitTx(transaction, TxHandlingOptions.None);
                if (result != AddTxResult.Added)
                {
                    throw new Exception("failed to add " + result);
                }
            }
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
                    LeavesInTransactionsAndBlocks = new[]
                    {
                        new[] // first block
                        {
                            TestItem.KeccakB // first transaction
                        }
                    },
                    LeavesInBlocksCounts = new[]
                    {
                        1 // tree count after first block
                    },
                    LeavesInMiddleOfReorganization = new Keccak[] { },
                    LeavesInAfterReorganization = new Keccak[] { },
                    FinalLeavesCount = 0
                };

                yield return new ReorganizedInsertLeafTest()
                {
                    LeavesInTransactionsAndBlocks = new[]
                    {
                        new[] // first block
                        {
                            TestItem.KeccakB // first transaction
                        }
                    },
                    LeavesInBlocksCounts = new[]
                    {
                        1 // tree count after first block
                    },
                    LeavesInMiddleOfReorganization = new[] { TestItem.KeccakD },
                    LeavesInAfterReorganization = new[] { TestItem.KeccakC },
                    FinalLeavesCount = 2
                };


                yield return new ReorganizedInsertLeafTest()
                {
                    LeavesInTransactionsAndBlocks = new[]
                    {
                        new[] // first block
                        {
                            TestItem.KeccakB // first transaction
                        },
                        new[] // second block
                        {
                            TestItem.KeccakH, // first transaction
                            TestItem.KeccakF // second transaction
                        }
                    },
                    LeavesInBlocksCounts = new[]
                    {
                        1, 3
                    },
                    LeavesInMiddleOfReorganization = new[] { TestItem.KeccakD, TestItem.KeccakA },
                    LeavesInAfterReorganization = new[] { TestItem.KeccakC },
                    FinalLeavesCount = 4
                };
            }
        }
    }
}
