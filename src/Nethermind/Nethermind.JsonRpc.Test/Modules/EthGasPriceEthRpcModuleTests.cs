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
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Facade;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules
{
    public partial class EthRpcModuleTests
    {
        [Test]
        public void eth_gas_price_get_right_percentile_with_blockcount_equal_to_blocks_to_check()
        {
            BlocktreeSetup blocktreeSetup = new BlocktreeSetup();
            ResultWrapper<UInt256?> resultWrapper = blocktreeSetup.ethRpcModule.eth_gasPrice();
            resultWrapper.Data.Should()
                .Be((UInt256?)2); //Tx Prices: 1,2,3,4,5,6, Index: (6-1)/5 = 1 => Gas Price should be 2
        }
        
        
        [Test]
        public void eth_gas_price_one_when_block_have_no_tx()
        {
            List<Tuple<int, Tuple<char, UInt256, UInt256>>> blocksAndTxsInfo;
            blocksAndTxsInfo = new[] {Tuple.Create(0, null),}
            Block[] blocks = BlocksBuilder(
                new Tuple<int, Tuple<char, UInt256, UInt256>[]>(0, null),
                (1, null),
                (2, null),
                (3, null),
                (4, null));
            Block a = Build.A.Block.Genesis.WithTransactions(Array.Empty<Transaction>())
                .TestObject;
            Block b = Build.A.Block.WithNumber(1).WithParentHash(a.Hash)
                .WithTransactions(Array.Empty<Transaction>()).TestObject;
            Block c = Build.A.Block.WithNumber(2).WithParentHash(b.Hash)
                .WithTransactions(Array.Empty<Transaction>()).TestObject;
            Block d = Build.A.Block.WithNumber(3).WithParentHash(c.Hash)
                .WithTransactions(Array.Empty<Transaction>()).TestObject;
            Block e = Build.A.Block.WithNumber(4).WithParentHash(d.Hash)
                .WithTransactions(Array.Empty<Transaction>()).TestObject;
            BlocktreeSetup blocktreeSetup = new BlocktreeSetup(new[] {a, b, c, d, e});

            ResultWrapper<UInt256?> resultWrapper = blocktreeSetup.ethRpcModule.eth_gasPrice();
            resultWrapper.Data.Should().Be((UInt256?)1);
        }

        [Test]
        public void Eth_gasPrice_ReturnDefaultGasPrice_EmptyBlocksAtEndGreaterThanOrEqualToEight()
        {
            Transaction[] transactions =
            {
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).WithGasPrice(2).WithNonce(0).TestObject,
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyB).WithGasPrice(3).WithNonce(0).TestObject,
            };

            Block a = Build.A.Block.Genesis.WithTransactions(transactions[0], transactions[1]).TestObject;
            Block b = Build.A.Block.WithNumber(1).WithParent(a).TestObject;
            Block c = Build.A.Block.WithNumber(2).WithParent(b).TestObject;
            Block d = Build.A.Block.WithNumber(3).WithParent(c).TestObject;
            Block e = Build.A.Block.WithNumber(4).WithParent(d).TestObject;
            Block f = Build.A.Block.WithNumber(5).WithParent(e).TestObject;
            Block g = Build.A.Block.WithNumber(6).WithParent(f).TestObject;
            Block h = Build.A.Block.WithNumber(7).WithParent(g).TestObject;
            Block i = Build.A.Block.WithNumber(8).WithParent(h).TestObject; //should return 1 since last

            BlocktreeSetup blocktreeSetup = new BlocktreeSetup(new[] {a, b, c, d, e, f, g, h});
            BlocktreeSetup blocktreeSetup2 = new BlocktreeSetup(new[] {a, b, c, d, e, f, g, h, i});

            blocktreeSetup.ethRpcModule.eth_gasPrice().Data.Should()
                .Be((UInt256?)3); //Gas Prices: 2,3,3,3,3,3,3,3,3 Index: 8 / 5 = 1.6, rounds to 2 => Gas Price is 3
            blocktreeSetup2.ethRpcModule.eth_gasPrice().Data.Should()
                .Be((UInt256?)1); //Last eight blocks empty, so gas price defaults to 1
        }

        [Test]
        public void Eth_gasPrice_getTxFromMinBlocks_NumTxGreaterThanOrEqualToLimit()
        {
            Transaction[] transactions =
            {
                //should i be worried about two tx with same hash?
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).WithGasPrice(1).WithNonce(0)
                    .TestObject,
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyB).WithGasPrice(2).WithNonce(0)
                    .TestObject,
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyC).WithGasPrice(3).WithNonce(0)
                    .TestObject,
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyD).WithGasPrice(4).WithNonce(0)
                    .TestObject,
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).WithGasPrice(5).WithNonce(1)
                    .TestObject,
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyB).WithGasPrice(7).WithNonce(1)
                    .TestObject,
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyB).WithGasPrice(6).WithNonce(2)
                    .TestObject,
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyB).WithGasPrice(9).WithNonce(3)
                    .TestObject,
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyD).WithGasPrice(8).WithNonce(1)
                    .TestObject,
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyC).WithGasPrice(11).WithNonce(1)
                    .TestObject,
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyD).WithGasPrice(11).WithNonce(2)
                    .TestObject,
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyC).WithGasPrice(11).WithNonce(2)
                    .TestObject //equal gas price to transactions[9] and transactions[10]
            };

            Block a = Build.A.Block.Genesis.WithTransactions(transactions[0], transactions[1])
                .TestObject;
            Block b = Build.A.Block.WithNumber(1).WithParentHash(a.Hash)
                .WithTransactions(transactions[2], transactions[3]).TestObject;
            Block c = Build.A.Block.WithNumber(2).WithParentHash(b.Hash)
                .WithTransactions(transactions[4], transactions[5]).TestObject;
            Block d = Build.A.Block.WithNumber(3).WithParentHash(c.Hash)
                .WithTransactions(transactions[6], transactions[7]).TestObject;
            Block e = Build.A.Block.WithNumber(4).WithParentHash(d.Hash)
                .WithTransactions(transactions[8], transactions[9]).TestObject;
            Block f = Build.A.Block.WithNumber(5).WithParentHash(e.Hash)
                .WithTransactions(transactions[10], transactions[11]).TestObject;
            BlocktreeSetup blocktreeSetup = new BlocktreeSetup(new[] {a, b, c, d, e, f});

            ResultWrapper<UInt256?> resultWrapper = blocktreeSetup.ethRpcModule.eth_gasPrice();
            resultWrapper.Data.Should()
                .Be((UInt256?)5); //Tx Prices: 3,4,5,6,7,8,9,10,11,12, Index: (10-1)/5 = 1.8, rounded to 2 => Gas Price should be 5
        }

        [Test]
        public void eth_gas_price_should_use_last_tx_price_when_head_block_is_not_changed()
        {
            BlocktreeSetup blocktreeSetup = new BlocktreeSetup();
            ResultWrapper<UInt256?> firstResult = blocktreeSetup.ethRpcModule.eth_gasPrice();
            ResultWrapper<UInt256?> secondResult = blocktreeSetup.ethRpcModule.eth_gasPrice();

            firstResult.Data.Should().Be(secondResult.Data);
            firstResult.ErrorCode.Should().Be(0);
            secondResult.ErrorCode.Should().Be(7);
        }

        [Test]
        public void eth_gas_price_should_remove_tx_when_txgasprices_are_under_threshold()
        {
            BlocktreeSetup blocktreeSetup = new BlocktreeSetup();
            Transaction a = Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyB).WithGasPrice(7).WithNonce(2)
                .TestObject;
            Transaction b = Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyB).WithGasPrice(8).WithNonce(3)
                .TestObject;

            Block a1 = Build.A.Block.WithNumber(5).WithTransactions(a, b)
                .WithParentHash(blocktreeSetup._blocks[^1].Hash).TestObject;
            BlocktreeSetup blockTreeSetup2 = new BlocktreeSetup(new[] {a1}, true);

            blocktreeSetup.ethRpcModule.eth_gasPrice(2).Data.Should()
                .Be((UInt256?)3); //Tx Prices: 2,3,4,5,6, Index (5-1)/5 => 0.8, rounded to 1 => price should be 3
            blockTreeSetup2.ethRpcModule.eth_gasPrice(3).Data.Should()
                .Be((UInt256?)4); //should only leave 3,4,5,6,7,8 => (7-1)/5 => 1.2, rounded to 1 => price should be 4
        }

        [Test]
        public void eth_gas_price_should_not_consider_eip1559_transactions()
        {
            BlocktreeSetup blocktreeSetup = new BlocktreeSetup();
            Transaction a = Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyB).WithType(TxType.EIP1559)
                .WithGasPrice(7).WithNonce(2)
                .TestObject;
            Transaction b = Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyB).WithType(TxType.EIP1559)
                .WithGasPrice(8).WithNonce(3)
                .TestObject;
            Transaction c = Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyB).WithType(TxType.EIP1559)
                .WithGasPrice(10).WithNonce(4)
                .TestObject;
            Transaction d = Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyB).WithType(TxType.EIP1559)
                .WithGasPrice(9).WithNonce(5)
                .TestObject;
            Transaction e = Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyB).WithType(TxType.EIP1559)
                .WithGasPrice(11).WithNonce(6)
                .TestObject;
            Block a1 = Build.A.Block.WithNumber(5).WithTransactions(a, b)
                .WithParentHash(blocktreeSetup._blocks[^1].Hash).TestObject;
            Block b1 = Build.A.Block.WithNumber(6).WithTransactions(c, d, e)
                .WithParentHash(a1.Hash).TestObject;
            BlocktreeSetup blockTreeSetup2 = new BlocktreeSetup(new[] {a1, b1}, true);

            blockTreeSetup2.ethRpcModule.eth_gasPrice().Data.Should()
                .Be((UInt256?)2); //should only leave 1,2,3,4,5,6 => (5-1)/5 => 1.2, rounded to 1 => price should be 2
        }

        [Test]
        public void eth_gas_price_get_tx_from_more_blocks_if_tx_count_not_greater_than_limit()
        {
            Transaction[] transactions =
            {
                //should i be worried about two tx with same hash?
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).WithGasPrice(1).WithNonce(0)
                    .TestObject,
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyB).WithGasPrice(2).WithNonce(0)
                    .TestObject,
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyC).WithGasPrice(3).WithNonce(0)
                    .TestObject,
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyD).WithGasPrice(4).WithNonce(0)
                    .TestObject,
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).WithGasPrice(5).WithNonce(1)
                    .TestObject,
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyB).WithGasPrice(7).WithNonce(1)
                    .TestObject,
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyB).WithGasPrice(6).WithNonce(2)
                    .TestObject,
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyB).WithGasPrice(9).WithNonce(3)
                    .TestObject,
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyD).WithGasPrice(8).WithNonce(1)
                    .TestObject,
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyC).WithGasPrice(11).WithNonce(1)
                    .TestObject,
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyD).WithGasPrice(10).WithNonce(2)
                    .TestObject,
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyC).WithGasPrice(12).WithNonce(2)
                    .TestObject,
            };

            Block a = Build.A.Block.Genesis.WithTransactions(transactions[0], transactions[1])
                .TestObject;
            Block b = Build.A.Block.WithNumber(1).WithParentHash(a.Hash)
                .WithTransactions(transactions[2], transactions[3]).TestObject; //1 block left
            Block c = Build.A.Block.WithNumber(2).WithParentHash(b.Hash)
                .WithTransactions(transactions[4])
                .TestObject; //1 block left (8 transactions added, 2 more to go => 8 + 2 >= 10)
            Block d = Build.A.Block.WithNumber(3).WithParentHash(c.Hash)
                .WithTransactions(transactions[5], transactions[6]).TestObject; //2 blocks left, I
            Block e = Build.A.Block.WithNumber(4).WithParentHash(d.Hash)
                .WithTransactions(transactions[7], transactions[8]).TestObject; //3 blocks left, I
            Block f = Build.A.Block.WithNumber(5).WithParentHash(e.Hash)
                .WithTransactions(transactions[9], transactions[10]).TestObject; //4 blocks left
            Block g = Build.A.Block.WithNumber(6).WithParentHash(f.Hash)
                .WithTransactions(transactions[11]).TestObject; //5 blocks left
            BlocktreeSetup blocktreeSetup = new BlocktreeSetup(new[] {a, b, c, d, e, f, g});

            ResultWrapper<UInt256?> resultWrapper = blocktreeSetup.ethRpcModule.eth_gasPrice();
            resultWrapper.Data.Should()
                .Be((UInt256?)5); //Tx Prices: 3,4,5,6,7,8,9,10,11,12, Index: (10 - 1)/5 = 1.8, rounded to 2 => Gas Price should be 5
        }

        [Test]
        public void eth_gas_price_blocks_available_less_than_blocks_to_check_should_be_successful()
        {
            Transaction[] transactions =
            {
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).WithGasPrice(1).WithNonce(0)
                    .TestObject,
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyB).WithGasPrice(2).WithNonce(0)
                    .TestObject,
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyC).WithGasPrice(3).WithNonce(0)
                    .TestObject,
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyD).WithGasPrice(5).WithNonce(0)
                    .TestObject,
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).WithGasPrice(4).WithNonce(1)
                    .TestObject,
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyB).WithGasPrice(6).WithNonce(1)
                    .TestObject
            };

            Block a = Build.A.Block.Genesis.WithTransactions(transactions[0], transactions[1])
                .TestObject;
            Block b = Build.A.Block.WithNumber(1).WithParentHash(a.Hash)
                .WithTransactions(transactions[2]).TestObject;
            Block c = Build.A.Block.WithNumber(2).WithParentHash(b.Hash)
                .WithTransactions(transactions[3]).TestObject;
            Block d = Build.A.Block.WithNumber(3).WithParentHash(c.Hash)
                .WithTransactions(transactions[4], transactions[5]).TestObject;

            BlocktreeSetup blocktreeSetup = new BlocktreeSetup(new[] {a, b, c, d});
            ResultWrapper<UInt256?> resultWrapper = blocktreeSetup.ethRpcModule.eth_gasPrice();
            resultWrapper.Data.Should()
                .Be((UInt256?)2); //Tx prices: 1,2,3,4,5,6, Index: (6-1)/5 = 1.2, rounded to 1 => price should be 2
        }

        public IEnumerable<Block> BlocksBuilder(KeyValuePair<int, string[][]>[] blockAndTxInfo)
        {
            Keccak parentHash = null;
            bool firstIteration = true;
            Block block;
            Transaction[] transactions;
            foreach (var keyValuePair in blockAndTxInfo)
            {
                block = BlockBuilder(keyValuePair, firstIteration, parentHash);
                parentHash = block.Hash;
                firstIteration = false;
                yield return block;
            }
        }

        private Block BlockBuilder(KeyValuePair<int, string[][]> keyValuePair, bool firstIteration, Keccak parentHash)
        {
            Transaction[] transactions;
            Block block;
            int blockNumber = keyValuePair.Key;
            string[][] txInfo = keyValuePair.Value; //array of tx info
            transactions = GetTransactionArray(txInfo);
            block = BlockFactoryNumberParentHashTxs(blockNumber, firstIteration ? null : parentHash, transactions);
            return block;
        }

        private Transaction[] GetTransactionArray(string[][] txInfo)
        {
            if (txInfo == null)
            {
                return Array.Empty<Transaction>();
            }
            else
            {
                return TxsFromInfoStrings(txInfo).ToArray();
            }
        }

        public IEnumerable<Transaction> TxsFromInfoStrings(params string[][] txsInfo)
        {
            PrivateKey privateKey;
            char privateKeyLetter;
            UInt256 gasPrice;
            UInt256 nonce;
            foreach (string[] txInfo in txsInfo)
            {
                privateKeyLetter = Convert.ToChar(txInfo[0]);
                privateKey = PrivateKeyForLetter(privateKeyLetter);
                gasPrice = UInt256.Parse(txInfo[1]);
                nonce = UInt256.Parse(txInfo[2]);
                yield return Build.A.Transaction.SignedAndResolved(privateKey).WithGasPrice(gasPrice).WithNonce(nonce)
                    .TestObject;
            }
        }
        
        public PrivateKey PrivateKeyForLetter(char privateKeyLetter)
        {
            if (privateKeyLetter == 'A')
            {
                return TestItem.PrivateKeyA;
            }
            else if (privateKeyLetter == 'B')
            {
                return TestItem.PrivateKeyB;
            }
            else if (privateKeyLetter == 'C')
            {
                return TestItem.PrivateKeyC;
            }
            else if (privateKeyLetter == 'D')
            {
                return TestItem.PrivateKeyD;
            }
            else
            {
                throw new ArgumentException("PrivateKeyLetter should only be either A, B, C, or D.");
            }
        }
        
        public Block BlockFactoryNumberParentHashTxs(int number, Keccak parentHash, Transaction[] txs)
        {
            if (number == 0)
            {
                return Build.A.Block.Genesis.WithTransactions(txs).TestObject;
            }

            else if (number > 0)
            {
                return Build.A.Block.WithNumber(number).WithParentHash(parentHash).WithTransactions(txs).TestObject;
            }
            
            else
            {
                throw new ArgumentException("Block number should be greater than or equal to 0.");
            }
        }
        public class BlocktreeSetup
        {
            private Transaction[] _transactions;
            public Block[] _blocks;
            public BlockTree blockTree;
            public EthRpcModule ethRpcModule;

            public BlocktreeSetup(Block[] blocks = null, bool addBlocks = false)
            {
                if (blocks == null || addBlocks)
                {
                    _transactions = new[]
                    {
                        //should i be worried about two tx with same hash?
                        Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).WithGasPrice(1).WithNonce(0)
                            .TestObject,
                        Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyB).WithGasPrice(2).WithNonce(0)
                            .TestObject,
                        Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyC).WithGasPrice(3).WithNonce(0)
                            .TestObject,
                        Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyD).WithGasPrice(5).WithNonce(0)
                            .TestObject,
                        Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).WithGasPrice(4).WithNonce(1)
                            .TestObject,
                        Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyB).WithGasPrice(6).WithNonce(1)
                            .TestObject,
                    };

                    Block a = Build.A.Block.Genesis.WithTransactions(_transactions[0], _transactions[1])
                        .TestObject;
                    Block b = Build.A.Block.WithNumber(1).WithParentHash(a.Hash)
                        .WithTransactions(_transactions[2]).TestObject;
                    Block c = Build.A.Block.WithNumber(2).WithParentHash(b.Hash)
                        .WithTransactions(_transactions[3]).TestObject;
                    Block d = Build.A.Block.WithNumber(3).WithParentHash(c.Hash)
                        .WithTransactions(_transactions[4]).TestObject;
                    Block e = Build.A.Block.WithNumber(4).WithParentHash(d.Hash)
                        .WithTransactions(_transactions[5])
                        .TestObject; //Tx Prices: 1,2,3,4,5,6, Index: (6-1)/5 = 1 => Gas Price should be 2 (if no tx added)
                    _blocks = new[] {a, b, c, d, e};
                    if (addBlocks)
                    {
                        List<Block> listBlocks = _blocks.ToList();
                        foreach (Block block in blocks)
                        {
                            listBlocks.Add(block);
                        }

                        _blocks = listBlocks.ToArray();
                    }
                }
                else
                {
                    _transactions = Array.Empty<Transaction>();
                    _blocks = blocks;
                }

                blockTree = Build.A.BlockTree(_blocks[0]).TestObject; //Genesis block not being added
                foreach (Block block in _blocks)
                {
                    BlockTreeBuilder.AddBlock(blockTree, block);
                }

                ethRpcModule = new EthRpcModule
                (
                    Substitute.For<IJsonRpcConfig>(),
                    Substitute.For<IBlockchainBridge>(),
                    blockTree,
                    Substitute.For<IStateReader>(),
                    Substitute.For<ITxPool>(),
                    Substitute.For<ITxSender>(),
                    Substitute.For<IWallet>(),
                    Substitute.For<ILogManager>(),
                    Substitute.For<ISpecProvider>()
                );
            }
        }
    }
}
