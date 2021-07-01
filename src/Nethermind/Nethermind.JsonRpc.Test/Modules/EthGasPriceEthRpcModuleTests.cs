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

            blockTreeSetup._ethRpcModule.eth_gasPrice();
            
            gasPriceOracle.Received(1).GasPriceEstimate();
        }

        [Test]
        public void Eth_gasPrice_DuplicateGasPrices_ReturnsSuccessfully()
        {
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup();
            Transaction[] dupGasPriceGroup =  GetTransactionsFromStringArray(
                    CollectTxStrings(
                    GetTxString("B","6","2"), 
                    GetTxString("B","6","3"),
                    GetTxString("B","6","4")),false);
            Block dupGasPriceBlock = GetBlockWithNumberParentHashAndTxInfo(5, HashOfLastBlockIn(blockTreeSetup),
                dupGasPriceGroup);
            blockTreeSetup = new BlockTreeSetup(new[]{dupGasPriceBlock},true);

            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup._ethRpcModule.eth_gasPrice();

            resultWrapper.Result.Should().Be(Result.Success);
        }
        [Test]
        public void Eth_gasPrice_BlockcountEqualToBlocksToCheck_ShouldGetTwentiethPercentileIndex()
        {
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup();
            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup._ethRpcModule.eth_gasPrice();
            resultWrapper.Data.Should().Be((UInt256?) 3); 
            //Tx Gas Prices: 1,2,3,4,5,6, Index: (6-1) * 2/5 = 2, Gas Price: 3
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
            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup._ethRpcModule.eth_gasPrice();
            
            resultWrapper.Data.Should().Be((UInt256?)1);
        }

        [TestCase(7,3)] //Tx Gas Prices: 2,3,3,3,3,3,3,3,3, Index: (9-1) * 2/5 rounds to 3, Gas Price: 3
        [TestCase(8,1)] //Last eight blocks empty, so gas price defaults to 1
        public void Eth_gasPrice_ReturnDefaultGasPrice_EmptyBlocksAtEndEqualToEight(int maxBlockNumber, int expected)
        {
            Block[] blocks = GetBlocks(
                GetBlockWithNumberAndTxInfo(0, CollectTxStrings(
                    GetTxString("A", "2", "0"),
                            GetTxString("B", "3", "0")
                        )),
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
            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup._ethRpcModule.eth_gasPrice();
            
            resultWrapper.Data.Should().Be((UInt256?) expected); 
        }
        
        [Test]
        public void Eth_gasPrice_GetTxFromMinBlocks_NumTxInMinBlocksGreaterThanOrEqualToLimit()
        {
            Block[] blocks = GetBlocks(
                GetBlockWithNumberAndTxInfo(0, CollectTxStrings(
                    GetTxString("A", "1", "0"),
                    GetTxString("B", "2", "0")
                    )
                ),
                GetBlockWithNumberAndTxInfo(1, CollectTxStrings(
                    GetTxString("C", "3", "0"),
                    GetTxString("D", "4", "0")
                    )
                ),
                GetBlockWithNumberAndTxInfo(2, CollectTxStrings(
                    GetTxString("A", "5","1"),
                    GetTxString("B", "6","1")
                    )
                )
            ); 
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(blocks: blocks, blockLimit: 2);
            
            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup._ethRpcModule.eth_gasPrice();
            resultWrapper.Data.Should().Be((UInt256?) 4); 
            //Tx Gas Prices: 3,4,5,6, Index: (4-1) * 2/5 rounds to 1, Gas Price: 4
        }

        [Test]
        public void Eth_gasPrice_WhenHeadBlockIsNotChanged_ShouldUsePreviouslyCalculatedGasPrice()
        {
            const int normalErrorCode = 0;
            int noHeadBlockChangeErrorCode = GasPriceOracle.NoHeadBlockChangeErrorCode;
            
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup();
            ResultWrapper<UInt256?> firstResult = blockTreeSetup._ethRpcModule.eth_gasPrice();
            ResultWrapper<UInt256?> secondResult = blockTreeSetup._ethRpcModule.eth_gasPrice();

            firstResult.Data.Should().Be(secondResult.Data);
            firstResult.ErrorCode.Should().Be(normalErrorCode);
            secondResult.ErrorCode.Should().Be(noHeadBlockChangeErrorCode);
        }

        [TestCase(2,4)] //Tx Gas Prices: 2,3,4,5,6, Index: (5-1) * 2/5 rounds to 2, Gas Price: 3
        [TestCase(4,6)] //Tx Gas Prices: 4,5,6,6,6, Index: (5-1) * 2/5 rounds to 2, Gas Price: 5
        public void Eth_gasPrice_TxGasPricesAreBelowThreshold_ReplaceGasPriceUnderThresholdWithLatestPrice(int ignoreUnder, int expected)
        {
            UInt256? ignoreUnderUInt256 = (UInt256) ignoreUnder;
            UInt256? expectedUInt256 = (UInt256) expected;
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(ignoreUnder: ignoreUnderUInt256);
            
            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup._ethRpcModule.eth_gasPrice();
            UInt256? result = resultWrapper.Data;
            
            result.Should().Be(expectedUInt256); 
        }

        [TestCase(false, 5)] //Tx Gas Prices: 1,2,3,4,5,6,9,10,11,11 Index: (10-1) * 2/5 rounds to 4, Gas Price: 5
        [TestCase(true, 3)]  //Tx Gas Prices: 0,0,1,2,3,4,5,6,9,10,11 Index: (11-1) * 2/5 = 4, Gas Price: 4
        public void Eth_gasPrice_InEip1559Mode_ShouldCalculateTxGasPricesDifferently(bool eip1559Enabled, int expected)
        {
            Transaction[] eip1559TxGroup =  GetTransactionsFromStringArray(CollectTxStrings(
                    GetTxString("B","7","2"), 
                    GetTxString("B","8","3")),
                IsEip1559());
            Transaction[] notEip1559TxGroup = GetTransactionsFromStringArray(CollectTxStrings(
                    GetTxString("B","9","4"),
                    GetTxString("B","10","5"),
                    GetTxString("B","11","6")),
                IsNotEip1559());
            
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup();
            Block firstBlock = GetBlockWithNumberParentHashAndTxInfo(5, HashOfLastBlockIn(blockTreeSetup), eip1559TxGroup);
            Block secondBlock = GetBlockWithNumberParentHashAndTxInfo(6, firstBlock.Hash, notEip1559TxGroup);
            blockTreeSetup = new BlockTreeSetup(new Block[]{firstBlock, secondBlock},true, 
                eip1559Enabled: eip1559Enabled);

            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup._ethRpcModule.eth_gasPrice();
            
            resultWrapper.Data.Should().Be((UInt256?) expected); 
        }

        private bool IsEip1559()
        {
            return true;
        }

        private bool IsNotEip1559()
        {
            return false;
        }

        private Keccak HashOfLastBlockIn(BlockTreeSetup blockTreeSetup)
        {
            return blockTreeSetup._blocks[^1].Hash;
        }
        
        [Test]
        public void Eth_gasPrice_TxCountNotGreaterThanLimit_GetTxFromMoreBlocks()
        {
            Block[] blocks = GetBlocks(
                GetBlockWithNumberAndTxInfo(0, CollectTxStrings(
                        GetTxString("A", "0", "0"),
                        GetTxString("B", "1", "0")
                    )
                ),
                GetBlockWithNumberAndTxInfo(1, CollectTxStrings(
                        GetTxString("C", "2", "0"),
                        GetTxString("D", "3","0")
                    )
                ),
                GetBlockWithNumberAndTxInfo(2, CollectTxStrings(
                    GetTxString("A", "4", "0")
                    )
                ));
            
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(blocks: blocks, blockLimit: 2);
            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup._ethRpcModule.eth_gasPrice();
            
            resultWrapper.Data.Should().Be((UInt256?) 2); 
            //Tx Gas Prices: 0,1,2,3,4, Index: (5-1) * 2/5 rounds to 2, Gas Price: 2
        }

        [Test]
        public void Eth_gasPrice_BlocksAvailableLessThanBlocksToCheck_ShouldBeSuccessful()
        {
            Block[] blocks = GetBlocks(
                GetBlockWithNumberAndTxInfo(0, CollectTxStrings(
                        GetTxString("A", "3", "0"),
                        GetTxString("B", "4", "0")
                    )
                ),
                GetBlockWithNumberAndTxInfo(1, CollectTxStrings(
                        GetTxString("C", "5", "0"),
                        GetTxString("D", "6","0")
                    )
                ),
                GetBlockWithNumberAndTxInfo(2, CollectTxStrings(
                        GetTxString("A", "7", "0"),
                        GetTxString("B", "8", "1")
                    )
                ));


            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(blocks: blocks, blockLimit: 4);
            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup._ethRpcModule.eth_gasPrice();
            
            resultWrapper.Data.Should().Be((UInt256?) 5); 
            //Tx Gas Prices: 3,4,5,6,7,8 Index: (6-1) * 2/5 = 2, Gas Price: 5
        }

        private string[][] CollectTxStrings(params string[][] txInfo)
        {
            return txInfo;
        }

        private string[] GetTxString(string privateKeyLetter, string gasPrice, string nonce)
        {
            return new[] {privateKeyLetter, gasPrice, nonce};
        }

        private KeyValuePair<int, string[][]> GetBlockWithNumberAndTxInfo(int blockNumber, string[][] txInfo)
        {
            return new KeyValuePair<int, string[][]>(blockNumber, txInfo);
        }

        private Block[] GetBlocks(params KeyValuePair<int, string[][]>[] blockAndTxInfo)
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
            string[][] txInfoArray = keyValuePair.Value;
            transactions = GetTransactionsFromStringArray(txInfoArray, isEip1559);
            block = GetBlockWithNumberParentHashAndTxInfo(blockNumber, parentHash, transactions);
            return block;
        }

        private Transaction[] GetTransactionsFromStringArray(string[][] txInfo, bool isEip1559)
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

        private IEnumerable<Transaction> Eip1559TxsFromInfoStrings(params string[][] txsInfo)
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

        private IEnumerable<Transaction> TxsFromInfoStrings(params string[][] txsInfo)
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

        private PrivateKey PrivateKeyForLetter(char privateKeyLetter)
        {
            switch (privateKeyLetter)
            {
                case 'A':
                    return TestItem.PrivateKeyA;
                case 'B':
                    return TestItem.PrivateKeyB;
                case 'C':
                    return TestItem.PrivateKeyC;
                case 'D':
                    return TestItem.PrivateKeyD;
                default:
                    throw new ArgumentException("PrivateKeyLetter should only be either A, B, C, or D.");
            }
        }

        private Block GetBlockWithNumberParentHashAndTxInfo(int number, Keccak parentHash, Transaction[] txs)
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
            private BlockTree _blockTree;
            public EthRpcModule _ethRpcModule;

            public BlockTreeSetup(Block[] blocks = null,  bool addBlocks = false, IGasPriceOracle gasPriceOracle = null, 
                int? blockLimit = null, UInt256? ignoreUnder = null, UInt256? baseFee = null, bool eip1559Enabled = false)
            {
                GetBlocks(blocks, addBlocks);

                InitializeAndAddToBlockTree();

                GetEthRpcModule(gasPriceOracle, ignoreUnder, blockLimit, baseFee, eip1559Enabled);
            }

            private void InitializeAndAddToBlockTree()
            {
                _blockTree = BuildABlockTreeWithGenesisBlock(_blocks[0]);
                foreach (Block block in _blocks)
                {
                    BlockTreeBuilder.AddBlock(_blockTree, block);
                }
            }

            private void GetBlocks(Block[] blocks, bool addToBlocks)
            {
                if (NoBlocksGiven(blocks) || addToBlocks)
                {
                    GetBlockArray();
                    if (addToBlocks)
                    {
                        AddExtraBlocksToArray(blocks);
                    }
                }
                else
                {
                    _blocks = blocks;
                }
            }

            private static bool NoBlocksGiven(Block[] blocks)
            {
                return blocks == null;
            }

            private BlockTree BuildABlockTreeWithGenesisBlock(Block genesisBlock)
            {
                return Build.A.BlockTree(genesisBlock).TestObject;
            }

            private void GetBlockArray()
            {
                EthRpcModuleTests e = new EthRpcModuleTests();
                _blocks = e.GetBlocks(
                    e.GetBlockWithNumberAndTxInfo(0, e.CollectTxStrings(
                            e.GetTxString("A", "1", "0"),
                            e.GetTxString("B", "2", "0")
                        )
                    ),
                    e.GetBlockWithNumberAndTxInfo(1, e.CollectTxStrings(
                            e.GetTxString("C", "3", "0")
                        )
                    ),
                    e.GetBlockWithNumberAndTxInfo(2, e.CollectTxStrings(
                            e.GetTxString("D", "5", "0")
                        )
                    ),
                    e.GetBlockWithNumberAndTxInfo(3, e.CollectTxStrings(
                            e.GetTxString("A", "4", "1")
                        )
                    ),
                    e.GetBlockWithNumberAndTxInfo(4, e.CollectTxStrings(
                            e.GetTxString("B", "6", "1")
                        )
                    )
                );
            }

            private void GetEthRpcModule(IGasPriceOracle? gasPriceOracle, UInt256? ignoreUnder, 
                int? blockLimit, UInt256? baseFee, bool eip1559Enabled)
            {
                _ethRpcModule = new EthRpcModule
                (
                    Substitute.For<IJsonRpcConfig>(),
                    Substitute.For<IBlockchainBridge>(),
                    _blockTree,
                    Substitute.For<IStateReader>(),
                    Substitute.For<ITxPool>(),
                    Substitute.For<ITxSender>(),
                    Substitute.For<IWallet>(),
                    Substitute.For<ILogManager>(),
                    Substitute.For<ISpecProvider>(),
                    eip1559Enabled,
                    gasPriceOracle,
                    ignoreUnder,
                    blockLimit,
                    baseFee
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
