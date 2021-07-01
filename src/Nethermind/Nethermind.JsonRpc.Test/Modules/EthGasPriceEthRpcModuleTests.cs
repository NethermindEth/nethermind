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
using Nethermind.Blockchain.Find;
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

            blockTreeSetup.EthRpcModule.eth_gasPrice();
            
            gasPriceOracle.Received(1).GasPriceEstimate(Arg.Any<IBlockFinder>());
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

            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup.EthRpcModule.eth_gasPrice();

            resultWrapper.Result.Should().Be(Result.Success);
        }
        [Test]
        public void Eth_gasPrice_BlockcountEqualToBlocksToCheck_ShouldGetTwentiethPercentileIndex()
        {
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup();
            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup.EthRpcModule.eth_gasPrice();
            resultWrapper.Data.Should().Be((UInt256?) 4); 
            //Tx Gas Prices: 1,2,3,4,5,6, Index: (6-1) * 3/5 = 3, Gas Price: 4
        }

        [Test]
        public void Eth_gasPrice_EstimatedGasPriceMoreThanMaxGasPrice_ReturnMaxGasPrice()
        {
            Block[] blockArray = GetBlocks(
                GetBlockKeyValuePairNumberAndTxInfo(0, CollectTxStrings(
                    GetTxString("A", "501", "0")
                    )
                ));
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(blockArray);

            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup.EthRpcModule.eth_gasPrice();
            resultWrapper.Data.Should().Be((UInt256?) 500);
        }

        [Test]
        public void Eth_gas_price_BlockWithMoreThanThreeTxs_OnlyAddsThreeGasPriceTxs()
        {
            Block[] blockArray = GetBlocks(
                GetBlockKeyValuePairNumberAndTxInfo(0, CollectTxStrings(
                    GetTxString("A", "4", "0"),
                    GetTxString("B", "3", "0"),
                    GetTxString("C", "2", "0"),
                    GetTxString("D", "1", "0")
                    )
                ));
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(blockArray);

            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup.EthRpcModule.eth_gasPrice();
            List<UInt256> gasPriceList = blockTreeSetup.GasPriceOracle.TxGasPriceList;
            gasPriceList.Count.Should().Be(3);
        }
        
       [Test] 
        public void Eth_gas_price_BlocksWithMoreThanThreeTxs_OnlyAddsThreeLowestEffectiveGasPriceTxs()
        {
            Block[] blockArray = GetBlocks(
                GetBlockKeyValuePairNumberAndTxInfo(0, CollectTxStrings(
                    GetTxString("A", "4", "0"),
                    GetTxString("B", "3", "0"),
                    GetTxString("C", "2", "0"),
                    GetTxString("D", "1", "0")
                    )
                ),
                GetBlockKeyValuePairNumberAndTxInfo(1, CollectTxStrings(
                    GetTxString("A", "8", "1"),
                    GetTxString("B", "7", "1"),
                    GetTxString("C", "6", "1"),
                    GetTxString("D", "5", "1")
                    )
                ));
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(blockArray);
            
            blockTreeSetup.EthRpcModule.eth_gasPrice();
            List<UInt256> gasPriceList = blockTreeSetup.GasPriceOracle.TxGasPriceList;
            
            IEnumerable<UInt256> correctGasPriceList = new List<UInt256> {1,2,3,5,6,7};
            gasPriceList.Should().Equal(correctGasPriceList);
        }
        
        [Test]
        public void Eth_gasPrice_WhenBlocksHaveNoTx_GasPriceShouldBeOne()
        {
            Block[] blocks = GetBlocks(
                    GetBlockKeyValuePairNumberAndTxInfo(0, null), 
                    GetBlockKeyValuePairNumberAndTxInfo(1, null),
                    GetBlockKeyValuePairNumberAndTxInfo(2, null),
                    GetBlockKeyValuePairNumberAndTxInfo(3, null),
                    GetBlockKeyValuePairNumberAndTxInfo(4, null));
            
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(blocks);
            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup.EthRpcModule.eth_gasPrice();
            
            resultWrapper.Data.Should().Be((UInt256?)1);
        }

        [TestCase(7,3)] //Tx Gas Prices: 2,3,3,3,3,3,3,3,3, Index: (9-1) * 2/5 rounds to 3, Gas Price: 3
        [TestCase(8,1)] //Last eight blocks empty, so gas price defaults to 1
        public void Eth_gasPrice_ReturnDefaultGasPrice_EmptyBlocksAtEndEqualToEight(int maxBlockNumber, int expected)
        {
            Block[] blocks = GetBlocks(
                GetBlockKeyValuePairNumberAndTxInfo(0, CollectTxStrings(
                    GetTxString("A", "2", "0"),
                            GetTxString("B", "3", "0")
                        )),
                GetBlockKeyValuePairNumberAndTxInfo(1, null),
                GetBlockKeyValuePairNumberAndTxInfo(2, null),
                GetBlockKeyValuePairNumberAndTxInfo(3, null),
                GetBlockKeyValuePairNumberAndTxInfo(4, null),
                GetBlockKeyValuePairNumberAndTxInfo(5, null),
                GetBlockKeyValuePairNumberAndTxInfo(6, null),
                GetBlockKeyValuePairNumberAndTxInfo(7, null),
                GetBlockKeyValuePairNumberAndTxInfo(8, null));

            IEnumerable<Block> blocksInRange = blocks.Where(block => block.Number <= maxBlockNumber);
            Block[] blockArray = blocksInRange.ToArray();
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(blockArray);
            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup.EthRpcModule.eth_gasPrice();
            
            resultWrapper.Data.Should().Be((UInt256?) expected); 
        }

        [Test]
        public void Eth_gasPrice_WhenHeadBlockIsNotChanged_ShouldUsePreviouslyCalculatedGasPrice()
        {
            const int normalErrorCode = 0;
            int noHeadBlockChangeErrorCode = GasPriceConfig.NoHeadBlockChangeErrorCode;
            
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup();
            ResultWrapper<UInt256?> firstResult = blockTreeSetup.EthRpcModule.eth_gasPrice();
            ResultWrapper<UInt256?> secondResult = blockTreeSetup.EthRpcModule.eth_gasPrice();

            firstResult.Data.Should().Be(secondResult.Data);
            firstResult.ErrorCode.Should().Be(normalErrorCode);
            secondResult.ErrorCode.Should().Be(noHeadBlockChangeErrorCode);
        }

        [TestCase(2,4)] //Tx Gas Prices: 2,3,4,5,6, Index: (5-1) * 3/5 rounds to 2, Gas Price: 3
        [TestCase(4,6)] //Tx Gas Prices: 4,5,6,6,6, Index: (5-1) * 3/5 rounds to 2, Gas Price: 5
        public void Eth_gasPrice_TxGasPricesAreBelowThreshold_ReplaceGasPriceUnderThresholdWithLatestPrice(int ignoreUnder, int expected)
        {
            UInt256? ignoreUnderUInt256 = (UInt256) ignoreUnder;
            UInt256? expectedUInt256 = (UInt256) expected;
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(ignoreUnder: ignoreUnderUInt256);
            
            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup.EthRpcModule.eth_gasPrice();
            UInt256? result = resultWrapper.Data;
            
            result.Should().Be(expectedUInt256); 
        }

        [TestCase(false, 6)] //Tx Gas Prices: 1,2,3,4,5,6,9,10,11,11 Index: (10-1) * 3/5 rounds to 5, Gas Price: 6
        [TestCase(true, 5)]  //Tx Gas Prices: 0,0,1,2,3,4,5,6,9,10,11 Index: (11-1) * 3/5 = 6, Gas Price: 5
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
            Block eip1559Block = GetBlockWithNumberParentHashAndTxInfo(5, HashOfLastBlockIn(blockTreeSetup), eip1559TxGroup);
            Block nonEip1559Block = GetBlockWithNumberParentHashAndTxInfo(6, eip1559Block.Hash, notEip1559TxGroup);
            blockTreeSetup = new BlockTreeSetup(new Block[]{eip1559Block, nonEip1559Block},true, 
                eip1559Enabled: eip1559Enabled);

            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup.EthRpcModule.eth_gasPrice();
            
            resultWrapper.Data.Should().Be((UInt256?) expected); 
        }

        [TestCase(true, 8)]
        [TestCase(false, 6)]
        public void Eth_gasPrice_LatestTxIsEip1559_ShouldOnlySetPriceAsDefaultWhenInEip1559Mode(bool eip1559Enabled, int expected)
        {
            Transaction[] eip1559TxGroup =  GetTransactionsFromStringArray(CollectTxStrings(
                    GetTxString("B","7","2"), 
                    GetTxString("B","8","3")),
                IsEip1559());
            
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup();
            Block eip1559Block = GetBlockWithNumberParentHashAndTxInfo(5, HashOfLastBlockIn(blockTreeSetup), eip1559TxGroup);
            blockTreeSetup = new BlockTreeSetup(new Block[]{eip1559Block},true, 
                eip1559Enabled: eip1559Enabled);

            blockTreeSetup.EthRpcModule.eth_gasPrice();

            blockTreeSetup.GasPriceOracle.DefaultGasPrice.Should().Be((UInt256?) expected); 
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
            return blockTreeSetup.Blocks[^1].Hash;
        }
        
        [Test]
        public void Eth_gasPrice_TxCountNotGreaterThanLimit_GetTxFromMoreBlocks()
        {
            Block[] blocks = GetBlocks(
                GetBlockKeyValuePairNumberAndTxInfo(0, CollectTxStrings(
                        GetTxString("A", "0", "0"),
                        GetTxString("B", "1", "0")
                    )
                ),
                GetBlockKeyValuePairNumberAndTxInfo(1, CollectTxStrings(
                        GetTxString("C", "2", "0"),
                        GetTxString("D", "3","0")
                    )
                ),
                GetBlockKeyValuePairNumberAndTxInfo(2, CollectTxStrings(
                    GetTxString("A", "4", "0")
                    )
                ));
            
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(blocks: blocks, blockLimit: 2);
            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup.EthRpcModule.eth_gasPrice();
            
            resultWrapper.Data.Should().Be((UInt256?) 2); 
            //Tx Gas Prices: 0,1,2,3,4, Index: (5-1) * 3/5 rounds to 2, Gas Price: 2
        }

        [Test]
        public void Eth_gasPrice_BlocksAvailableLessThanBlocksToCheck_ShouldBeSuccessful()
        {
            Block[] blocks = GetBlocks(
                GetBlockKeyValuePairNumberAndTxInfo(0, CollectTxStrings(
                        GetTxString("A", "3", "0"),
                        GetTxString("B", "4", "0")
                    )
                ),
                GetBlockKeyValuePairNumberAndTxInfo(1, CollectTxStrings(
                        GetTxString("C", "5", "0"),
                        GetTxString("D", "6","0")
                    )
                ),
                GetBlockKeyValuePairNumberAndTxInfo(2, CollectTxStrings(
                        GetTxString("A", "7", "0"),
                        GetTxString("B", "8", "1")
                    )
                ));


            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(blocks: blocks, blockLimit: 4);
            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup.EthRpcModule.eth_gasPrice();
            
            resultWrapper.Data.Should().Be((UInt256?) 6); 
            //Tx Gas Prices: 3,4,5,6,7,8 Index: (6-1) * 3/5 = 3, Gas Price: 6
        }
        
        [Test]
        public void Eth_gasPrice_GetTxFromMinBlocks_NumTxInMinBlocksGreaterThanOrEqualToLimit()
        {
            Block[] blocks = GetBlocks(
                GetBlockKeyValuePairNumberAndTxInfo(0, CollectTxStrings(
                    GetTxString("A", "1", "0"),
                    GetTxString("B", "2", "0")
                    )
                ),
                GetBlockKeyValuePairNumberAndTxInfo(1, CollectTxStrings(
                    GetTxString("C", "3", "0"),
                    GetTxString("D", "4", "0")
                    )
                ),
                GetBlockKeyValuePairNumberAndTxInfo(2, CollectTxStrings(
                    GetTxString("A", "5","1"),
                    GetTxString("B", "6","1")
                    )
                )
            ); 
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(blocks: blocks, blockLimit: 2);
            
            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup.EthRpcModule.eth_gasPrice();
            resultWrapper.Data.Should().Be((UInt256?) 5); 
            //Tx Gas Prices: 3,4,5,6, Index: (4-1) * 3/5 rounds to 2, Gas Price: 5
        }
        
        [Test]
        public void Eth_gasPrice_TransactionSentByMiner_AreNotConsideredInGasPriceCalculation()
        {
            Address minerAddress = PrivateKeyForLetter('A').Address;
            Block block = GetBlockWithBeneficiaryBlockNumberAndTxInfo(minerAddress, 0, 
                CollectTxStrings(
                        GetTxString("A", "7", "0"),
                        GetTxString("B", "8", "0"),
                        GetTxString("C", "9", "0")
                    )
                );


            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(new[]{block});
            blockTreeSetup.EthRpcModule.eth_gasPrice();

            List<UInt256> gasPriceList = blockTreeSetup.GasPriceOracle.TxGasPriceList;
            List<UInt256> expected = new List<UInt256>{8,9};
            gasPriceList.Should().Equal(expected);
        }
        private string[][] CollectTxStrings(params string[][] txInfo)
        {
            return txInfo;
        }

        private string[] GetTxString(string privateKeyLetter, string gasPrice, string nonce)
        {
            return new[] {privateKeyLetter, gasPrice, nonce};
        }

        private KeyValuePair<int, string[][]> GetBlockKeyValuePairNumberAndTxInfo(int blockNumber, string[][] txInfo)
        {
            return new KeyValuePair<int, string[][]>(blockNumber, txInfo);
        }
        
        private Block GetBlockWithBeneficiaryBlockNumberAndTxInfo(Address beneficiary, int blockNumber, string[][] txInfo)
        {
            Transaction[] transactions = GetTransactionsFromStringArray(txInfo, false);
            return Build.A.Block.WithBeneficiary(beneficiary).WithNumber(blockNumber).WithTransactions(transactions)
                .TestObject;
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
            public Block[] Blocks { get; private set; }
            public BlockTree BlockTree { get; private set; }
            public EthRpcModule EthRpcModule { get; private set; }
            public IGasPriceOracle GasPriceOracle { get; private set; }

            public BlockTreeSetup(Block[] blocks = null,  bool addBlocks = false, IGasPriceOracle? gasPriceOracle = null, 
                int? blockLimit = null, UInt256? ignoreUnder = null, UInt256? baseFee = null, bool eip1559Enabled = false)
            {
                GetBlocks(blocks, addBlocks);

                InitializeAndAddToBlockTree();

                GasPriceOracle = gasPriceOracle ?? GetGasPriceOracle(eip1559Enabled, ignoreUnder, blockLimit, baseFee);
                
                GetEthRpcModule(GasPriceOracle, ignoreUnder, blockLimit, baseFee, eip1559Enabled);
            }

            private void InitializeAndAddToBlockTree()
            {
                BlockTree = BuildABlockTreeWithGenesisBlock(Blocks[0]);
                foreach (Block block in Blocks)
                {
                    BlockTreeBuilder.AddBlock(BlockTree, block);
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
                    Blocks = blocks;
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
                Blocks = e.GetBlocks(
                    e.GetBlockKeyValuePairNumberAndTxInfo(0, e.CollectTxStrings(
                            e.GetTxString("A", "1", "0"),
                            e.GetTxString("B", "2", "0")
                        )
                    ),
                    e.GetBlockKeyValuePairNumberAndTxInfo(1, e.CollectTxStrings(
                            e.GetTxString("C", "3", "0")
                        )
                    ),
                    e.GetBlockKeyValuePairNumberAndTxInfo(2, e.CollectTxStrings(
                            e.GetTxString("D", "5", "0")
                        )
                    ),
                    e.GetBlockKeyValuePairNumberAndTxInfo(3, e.CollectTxStrings(
                            e.GetTxString("A", "4", "1")
                        )
                    ),
                    e.GetBlockKeyValuePairNumberAndTxInfo(4, e.CollectTxStrings(
                            e.GetTxString("B", "6", "1")
                        )
                    )
                );
            }

            private void GetEthRpcModule(IGasPriceOracle? gasPriceOracle, UInt256? ignoreUnder, 
                int? blockLimit, UInt256? baseFee, bool eip1559Enabled)
            {
                EthRpcModule = new EthRpcModule
                (
                    Substitute.For<IJsonRpcConfig>(),
                    Substitute.For<IBlockchainBridge>(),
                    BlockTree,
                    Substitute.For<IStateReader>(),
                    Substitute.For<ITxPool>(),
                    Substitute.For<ITxSender>(),
                    Substitute.For<IWallet>(),
                    Substitute.For<ILogManager>(),
                    Substitute.For<ISpecProvider>(),
                    GasPriceOracle
                );
            }
            private void AddExtraBlocksToArray(Block[] blocks)
            {
                List<Block> listBlocks = Blocks.ToList();
                foreach (Block block in blocks)
                {
                    listBlocks.Add(block);
                }

                Blocks = listBlocks.ToArray();
            }
            
            private IGasPriceOracle GetGasPriceOracle(bool eip1559Enabled, UInt256? ignoreUnder,
                int? blockLimit, UInt256? baseFee)
            {
                GasPriceOracle gasPriceOracle = new GasPriceOracle(eip1559Enabled, ignoreUnder, blockLimit, baseFee);
                return gasPriceOracle;
            }
        }
    }
}
