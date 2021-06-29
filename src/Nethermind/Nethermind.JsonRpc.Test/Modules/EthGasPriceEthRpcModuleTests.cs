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
        public void Eth_gasPrice_WhenCalled_CallsGasPriceEstimateFromGasPriceOracle()
        {
            IGasPriceOracle gasPriceOracle = Substitute.For<IGasPriceOracle>();
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(gasPriceOracle: gasPriceOracle);

            blockTreeSetup.ethRpcModule.eth_gasPrice();
            
            gasPriceOracle.Received(1).GasPriceEstimate();
        }

        [Test]
        public void Eth_gasPrice_BlockcountEqualToBlocksToCheck_ShouldGetTwentiethPercentileIndex()
        {
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup();
            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup.ethRpcModule.eth_gasPrice();
            resultWrapper.Data.Should()
                .Be((UInt256?)2); //Tx Prices: 1,2,3,4,5,6, Index: (6-1)/5 = 1 => Gas Price should be 2
        }


        [Test]
        public void Eth_gasPrice_WhenBlocksHaveNoTx_GasPriceShouldBeOne()
        {
            Block[] blocks = GetBlocks(
                    GetBlockWithNumberAndTxInfo(0, null), 
                    GetBlockWithNumberAndTxInfo(1, null),
                    GetBlockWithNumberAndTxInfo(2, null),
                    GetBlockWithNumberAndTxInfo(3, null),
                    GetBlockWithNumberAndTxInfo(4, null));
            
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(blocks);
            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup.ethRpcModule.eth_gasPrice();
            
            resultWrapper.Data.Should().Be((UInt256?)1);
        }

        [TestCase(7,3)] //Gas Prices: 2,3,3,3,3,3,3,3,3 Index: 8 / 5 rounds to 2 => Gas Price is 3
        [TestCase(8,1)] //Last eight blocks empty, so gas price defaults to 1
        public void Eth_gasPrice_ReturnDefaultGasPrice_EmptyBlocksAtEndEqualToEight(int maxBlockNumber, int expected)
        {
            Block[] blocks = GetBlocks(
                GetBlockWithNumberAndTxInfo(0, GetArray(
                        GetStringArray("A", "2", "0"),
                            GetStringArray("B", "3", "0")
                        )
                    ),
                GetBlockWithNumberAndTxInfo(1, null),
                GetBlockWithNumberAndTxInfo(2, null),
                GetBlockWithNumberAndTxInfo(3, null),
                GetBlockWithNumberAndTxInfo(4, null),
                GetBlockWithNumberAndTxInfo(5, null),
                GetBlockWithNumberAndTxInfo(6, null),
                GetBlockWithNumberAndTxInfo(7, null),
                GetBlockWithNumberAndTxInfo(8, null));

            IEnumerable<Block> blocksInRange = blocks.Where(block => block.Number <= maxBlockNumber);
            Block[] blockArray = blocksInRange.ToArray();
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(blockArray);
            UInt256? blockTreeSetupResult = blockTreeSetup.ethRpcModule.eth_gasPrice().Data;
            
            blockTreeSetupResult.Should().Be((UInt256?) expected); 
        }
        //Test for repeated Tx
        [Test]
        public void Eth_gasPrice_GetTxFromMinBlocks_NumTxGreaterThanOrEqualToLimit()
        {
            Block[] blocks = GetBlocks(
                GetBlockWithNumberAndTxInfo(0, GetArray(
                    GetStringArray("A", "1", "0"),
                    GetStringArray("B", "2", "0")
                    )
                ),
                GetBlockWithNumberAndTxInfo(1, GetArray(
                    GetStringArray("C", "3", "0"),
                    GetStringArray("D", "4", "0")
                    )
                ),
                GetBlockWithNumberAndTxInfo(2, GetArray(
                    GetStringArray("A", "5","1"),
                    GetStringArray("B", "6","1")
                    )
                )
            ); 
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(blocks: blocks, blockLimit: 2);
            
            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup.ethRpcModule.eth_gasPrice();
            resultWrapper.Data.Should().Be((UInt256?) 4); 
            //Tx Prices: 3,4,5,6 Index: (4-1)/5 rounds to 1 Gas Price: 4
        }

        [Test]
        public void Eth_gasPrice_WhenHeadBlockIsNotChanged_ShouldUsePreviouslyCalculatedGasPrice()
        {
            const int normalErrorCode = 0;
            int noHeadBlockChangeErrorCode = GasPriceOracle.NoHeadBlockChangeErrorCode;
            
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup();
            ResultWrapper<UInt256?> firstResult = blockTreeSetup.ethRpcModule.eth_gasPrice();
            ResultWrapper<UInt256?> secondResult = blockTreeSetup.ethRpcModule.eth_gasPrice();

            firstResult.Data.Should().Be(secondResult.Data);
            firstResult.ErrorCode.Should().Be(normalErrorCode);
            secondResult.ErrorCode.Should().Be(noHeadBlockChangeErrorCode);
        }

        [TestCase(2,3)] //Tx Prices: 2,3,4,5,6 Index: (5-1)/5 rounds to 1 => price should be 3
        [TestCase(4,5)] //Tx Prices: 4,5,6,6,6 Index: (5-1)/5 rounds to 1 => price should be 5
        public void Eth_gasPrice_TxGasPricesAreBelowThreshold_ReplaceGasPriceUnderThresholdWithLatestPrice(int ignoreUnder, int expected)
        {
            UInt256? ignoreUnderUInt256 = (UInt256) ignoreUnder;
            UInt256? expectedUInt256 = (UInt256) expected;
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(ignoreUnder: ignoreUnderUInt256);
            
            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup.ethRpcModule.eth_gasPrice();
            UInt256? result = resultWrapper.Data;
            
            result.Should().Be(expectedUInt256); 
        }

        [Test]
        public void Eth_gasPrice_GivenEip1559Tx_ShouldNotConsiderEip1559Tx()
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
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup();
            Block firstBlock = Build.A.Block.WithNumber(5).WithParentHash(HashOfLastBlockIn(blockTreeSetup))
                .WithTransactions(firstTxGroup).TestObject;
            Block secondBlock = Build.A.Block.WithNumber(6).WithParentHash(firstBlock.Hash)
                .WithTransactions(firstTxGroup).TestObject;
            
            blockTreeSetup = new BlockTreeSetup(new Block[]{firstBlock, secondBlock},true);

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

        public Keccak HashOfLastBlockIn(BlockTreeSetup blockTreeSetup)
        {
            return blockTreeSetup._blocks[^1].Hash;
        }
        
        [Test]
        public void Eth_gasPrice_TxCountNotGreaterThanLimit_GetTxFromMoreBlocks()
        {
            Block[] blocks = GetBlocks(
                GetBlockWithNumberAndTxInfo(0, GetArray(
                        GetStringArray("A", "0", "0"),
                        GetStringArray("B", "1", "0")
                    )
                ),
                GetBlockWithNumberAndTxInfo(1, GetArray(
                        GetStringArray("C", "2", "0"),
                        GetStringArray("D", "3","0")
                    )
                ),
                GetBlockWithNumberAndTxInfo(2, GetArray(
                    GetStringArray("A", "4", "0")
                    )
                ));
            
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(blocks: blocks, blockLimit: 2);
            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup.ethRpcModule.eth_gasPrice();
            
            resultWrapper.Data.Should().Be((UInt256?) 1); 
            //Tx Prices: 0,1,2,3,4 Index: (5 - 1)/5 rounds to 1 Gas Price: 1
        }

        [Test]
        public void Eth_gasPrice_BlocksAvailableLessThanBlocksToCheck_ShouldBeSuccessful()
        {
            Block[] blocks = GetBlocks(
                GetBlockWithNumberAndTxInfo(0, GetArray(
                        GetStringArray("A", "3", "0"),
                        GetStringArray("B", "4", "0")
                    )
                ),
                GetBlockWithNumberAndTxInfo(1, GetArray(
                        GetStringArray("C", "5", "0"),
                        GetStringArray("D", "6","0")
                    )
                ),
                GetBlockWithNumberAndTxInfo(2, GetArray(
                        GetStringArray("A", "7", "0"),
                        GetStringArray("B", "8", "1")
                    )
                ));


            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(blocks: blocks, blockLimit: 4);
            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup.ethRpcModule.eth_gasPrice();
            
            resultWrapper.Data.Should().Be((UInt256?) 4); 
            //Tx prices: 3,4,5,6,7,8 Index: (6-1)/5 = 1 Gas Price: 4
        }

        public string[][] GetArray(params string[][] txInfo)
        {
            return txInfo;
        }

        public string[] GetStringArray(string privateKeyLetter, string gasPrice, string nonce)
        {
            return new[] {privateKeyLetter, gasPrice, nonce};
        }
        public KeyValuePair<int, string[][]> GetBlockWithNumberAndTxInfo(int blockNumber, string[][] txInfo)
        {
            return new KeyValuePair<int, string[][]>(blockNumber, txInfo);
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
        public class BlockTreeSetup
        {
            public Block[] _blocks;
            public BlockTree blockTree;
            public EthRpcModule ethRpcModule;

            public BlockTreeSetup(Block[] blocks = null, bool addBlocks = false, IGasPriceOracle gasPriceOracle = null, 
                int? blockLimit = null, UInt256? ignoreUnder = null)
            {
                GetBlocks(blocks, addBlocks);

                InitializeAndAddToBlockTree();

                GetEthRpcModule(gasPriceOracle, ignoreUnder, blockLimit);
            }

            private void InitializeAndAddToBlockTree()
            {
                blockTree = BuildABlockTreeWithGenesisBlock(_blocks[0]);
                foreach (Block block in _blocks)
                {
                    BlockTreeBuilder.AddBlock(blockTree, block);
                }
            }

            private void GetBlocks(Block[] blocks, bool addBlocks)
            {
                if (blocks == null || addBlocks)
                {
                    GetBlockArray();
                    if (addBlocks)
                    {
                        AddExtraBlocksToArray(blocks);
                    }
                }
                else
                {
                    _blocks = blocks;
                }
            }

            private BlockTree BuildABlockTreeWithGenesisBlock(Block genesisBlock)
            {
                return Build.A.BlockTree(genesisBlock).TestObject;
            }

            private void GetBlockArray()
            {
                EthRpcModuleTests e = new EthRpcModuleTests();
                _blocks = e.GetBlocks(
                    e.GetBlockWithNumberAndTxInfo(0, e.GetArray(
                            e.GetStringArray("A", "1", "0"),
                            e.GetStringArray("B", "2", "0")
                        )
                    ),
                    e.GetBlockWithNumberAndTxInfo(1, e.GetArray(
                            e.GetStringArray("C", "3", "0")
                        )
                    ),
                    e.GetBlockWithNumberAndTxInfo(2, e.GetArray(
                            e.GetStringArray("D", "5", "0")
                        )
                    ),
                    e.GetBlockWithNumberAndTxInfo(3, e.GetArray(
                            e.GetStringArray("A", "4", "1")
                        )
                    ),
                    e.GetBlockWithNumberAndTxInfo(4, e.GetArray(
                            e.GetStringArray("B", "6", "1")
                        )
                    )
                );
            }

            private void GetEthRpcModule(IGasPriceOracle? gasPriceOracle, UInt256? ignoreUnder, int? blockLimit)
            {
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
                    Substitute.For<ISpecProvider>(),
                    gasPriceOracle,
                    ignoreUnder,
                    blockLimit
                );
            }
            private void AddExtraBlocksToArray(Block[] blocks)
            {
                List<Block> listBlocks = _blocks.ToList();
                foreach (Block block in blocks)
                {
                    listBlocks.Add(block);
                }

                _blocks = listBlocks.ToArray();
            }
        }
    }
}
