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


        public string[][] GetArray(params string[][] txInfo)
        {
            return txInfo;
        }

        public string[] GetStringArray(string privateKeyLetter, string gasPrice, string nonce)
        {
            return new[] {privateKeyLetter, gasPrice, nonce};
        }
        public KeyValuePair<int, string[][]> GetBlockWithNumberAndTxs(int blockNumber, string[][] txInfo)
        {
            return new KeyValuePair<int, string[][]>(blockNumber, txInfo);
        }
        [Test]
        public void eth_gas_price_one_when_block_have_no_tx()
        {
            Block[] blocks = GetBlocks(
                    GetBlockWithNumberAndTxs(0, null), 
                    GetBlockWithNumberAndTxs(1, null),
                    GetBlockWithNumberAndTxs(2, null),
                    GetBlockWithNumberAndTxs(3, null),
                    GetBlockWithNumberAndTxs(4, null));
            
            BlocktreeSetup blocktreeSetup = new BlocktreeSetup(blocks);
            ResultWrapper<UInt256?> resultWrapper = blocktreeSetup.ethRpcModule.eth_gasPrice();
            
            resultWrapper.Data.Should().Be((UInt256?)1);
        }

        [TestCase(7,3)] //Gas Prices: 2,3,3,3,3,3,3,3,3 Index: 8 / 5 = 1.6, rounds to 2 => Gas Price is 3
        [TestCase(8,1)] //Last eight blocks empty, so gas price defaults to 1
        public void Eth_gasPrice_ReturnDefaultGasPrice_EmptyBlocksAtEndEqualToEight(int maxBlockNumber, int expected)
        {
            Block[] blocks = GetBlocks(
                GetBlockWithNumberAndTxs(0, GetArray(
                        GetStringArray("A", "2", "0"),
                            GetStringArray("B", "3", "0")
                        )
                    ),
                GetBlockWithNumberAndTxs(1, null),
                GetBlockWithNumberAndTxs(2, null),
                GetBlockWithNumberAndTxs(3, null),
                GetBlockWithNumberAndTxs(4, null),
                GetBlockWithNumberAndTxs(5, null),
                GetBlockWithNumberAndTxs(6, null),
                GetBlockWithNumberAndTxs(7, null),
                GetBlockWithNumberAndTxs(8, null));

            IEnumerable<Block> blocksInRange = blocks.Where(block => block.Number <= maxBlockNumber);
            Block[] blockArray = blocksInRange.ToArray();
            BlocktreeSetup blocktreeSetup = new BlocktreeSetup(blockArray);
            UInt256? blocktreeSetupResult = blocktreeSetup.ethRpcModule.eth_gasPrice().Data;
            
            blocktreeSetupResult.Should().Be((UInt256?) expected); 
        }
        //Test for repeated Tx
        [Test]
        public void Eth_gasPrice_getTxFromMinBlocks_NumTxGreaterThanOrEqualToLimit()
        {
            //should I change the limit? Override the gas Price class?
            Block[] blocks = GetBlocks(
                GetBlockWithNumberAndTxs(0, GetArray(
                    GetStringArray("A", "1", "0"),
                    GetStringArray("B", "2", "0")
                    )
                ),
                GetBlockWithNumberAndTxs(1, GetArray(
                    GetStringArray("C", "3", "0"),
                    GetStringArray("D", "4", "0")
                    )
                ),
                GetBlockWithNumberAndTxs(2, GetArray(
                    GetStringArray("A", "5","1"),
                    GetStringArray("B", "6","1")
                    )
                )
            ); 
            BlocktreeSetup blocktreeSetup = new BlocktreeSetup(blocks);
            
            ResultWrapper<UInt256?> resultWrapper = blocktreeSetup.ethRpcModule.eth_gasPrice(blockLimit: 2);
            resultWrapper.Data.Should()
                .Be((UInt256?) 4); //Tx Prices: 3,4,5,6 Index: (4-1)/5 = 0.6, rounded to 1 => Gas Price should be 4
        }

        [Test]
        public void eth_gas_price_should_use_last_tx_price_when_head_block_is_not_changed()
        {
            const int normalErrorCode = 0;
            int noHeadBlockChangeErrorCode = GasPriceOracle._noHeadBlockChangeErrorCode;
            
            BlocktreeSetup blocktreeSetup = new BlocktreeSetup();
            ResultWrapper<UInt256?> firstResult = blocktreeSetup.ethRpcModule.eth_gasPrice();
            ResultWrapper<UInt256?> secondResult = blocktreeSetup.ethRpcModule.eth_gasPrice();

            firstResult.Data.Should().Be(secondResult.Data);
            firstResult.ErrorCode.Should().Be(normalErrorCode);
            secondResult.ErrorCode.Should().Be(noHeadBlockChangeErrorCode);
        }

        [TestCase(2,3)] //Tx Prices: 2,3,4,5,6 Index: (5-1)/5 => 0.8, rounded to 1 => price should be 3
        [TestCase(4,5)] //Tx Prices: 4,5,6 Index: (3-1)/5 => 0.6, rounded to 1 => price should be 5
        public void eth_gasPrice_WhenTxGasPricesAreBelowThreshold_shouldNotConsiderTxsWithGasPriceUnderThreshold(int ignoreUnder, int expected)
        {
            BlocktreeSetup blocktreeSetup = new BlocktreeSetup();
            UInt256? ignoreUnderUInt256 = (UInt256) ignoreUnder;
            UInt256? expectedUInt256 = (UInt256) expected;
            
            ResultWrapper<UInt256?> resultWrapper = blocktreeSetup.ethRpcModule.eth_gasPrice(ignoreUnderUInt256);
            UInt256? result = resultWrapper.Data;
            
            result.Should().Be(expectedUInt256); 
        }

        [Test]
        public void eth_gasPrice_GivenEip1559Tx_ShouldNotConsiderEip1559Tx()
        {
            Transaction[] firstTxGroup =  GetTransactionArray(
                    GetArray(
                    GetStringArray("B","7","2"), 
                    GetStringArray("B","8","3")),IsEip1559());
            Transaction[] secondTxGroup = GetTransactionArray(
                    GetArray(
                    GetStringArray("B","9","4"),
                    GetStringArray("B","10","5"),
                    GetStringArray("B","11","6")),IsNotEip1559());
            BlocktreeSetup blocktreeSetup = new BlocktreeSetup();
            Block firstBlock = Build.A.Block.WithNumber(5).WithParentHash(HashOfLastBlockIn(blocktreeSetup))
                .WithTransactions(firstTxGroup).TestObject;
            Block secondBlock = Build.A.Block.WithNumber(6).WithParentHash(firstBlock.Hash)
                .WithTransactions(firstTxGroup).TestObject;
            
            BlocktreeSetup blockTreeSetup = new BlocktreeSetup(new Block[]{firstBlock, secondBlock},true);

            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup.ethRpcModule.eth_gasPrice();
            resultWrapper.Data.Should().Be((UInt256?) 2); 
            //should only leave 1,2,3,4,5,6 => (5-1)/5 => 1.2, rounded to 1 => price should be 2
        }

        public bool IsEip1559()
        {
            return true;
        }

        public bool IsNotEip1559()
        {
            return false;
        }

        public Keccak HashOfLastBlockIn(BlocktreeSetup blocktreeSetup)
        {
            return blocktreeSetup._blocks[^1].Hash;
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

        public Block[] GetBlocks(params KeyValuePair<int, string[][]>[] blockAndTxInfo)
        {
            Keccak parentHash = null;
            Block block;
            List<Block> blocks = new List<Block>();
            foreach (KeyValuePair<int, string[][]> keyValuePair in blockAndTxInfo)
            {
                block = BlockBuilder(keyValuePair, parentHash);
                parentHash = block.Hash;
                blocks.Add(block);
            }

            return blocks.ToArray();
        }
        
        
        public Block[] GetBlocksWithEip1559Txs(params KeyValuePair<int, string[][]>[] blockAndTxInfo)
        {
            Keccak parentHash = null;
            Block block;
            List<Block> blocks = new List<Block>();
            foreach (KeyValuePair<int, string[][]> keyValuePair in blockAndTxInfo)
            {
                block = BlockBuilder(keyValuePair, parentHash, true);
                parentHash = block.Hash;
                blocks.Add(block);
            }

            return blocks.ToArray();
        }

        private Block BlockBuilder(KeyValuePair<int, string[][]> keyValuePair, Keccak parentHash, bool isEip1559 = false)
        {
            Transaction[] transactions;
            Block block;
            
            int blockNumber = keyValuePair.Key;
            string[][] txInfo = keyValuePair.Value; //array of tx info
            transactions = GetTransactionArray(txInfo, isEip1559);
            block = BlockFactoryNumberParentHashTxs(blockNumber, parentHash, transactions);
            return block;
        }

        private Transaction[] GetTransactionArray(string[][] txInfo, bool isEip1559)
        {
            if (txInfo == null)
            {
                return Array.Empty<Transaction>();
            }
            else if (isEip1559 == true)
            {
                return Eip1559TxsFromInfoStrings(txInfo).ToArray();
            }
            else
            {
                return TxsFromInfoStrings(txInfo).ToArray();
            }
        }

        public IEnumerable<Transaction> Eip1559TxsFromInfoStrings(params string[][] txsInfo)
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
                    .WithType(TxType.EIP1559).TestObject;
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
                    BlockTreeBuilder.AddBlock(blockTree, block); //do we need to add genesis block?
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
