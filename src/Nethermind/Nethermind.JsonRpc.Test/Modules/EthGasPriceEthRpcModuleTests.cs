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
            Transaction[] dupGasPriceGroup =  GetTransactionsFromTxStrings(
                    CollectTxStrings(
                    GetTxString("B","6","2"), 
                    GetTxString("B","6","3"),
                    GetTxString("B","6","4")),IsNotEip1559());
            Block dupGasPriceBlock = GetBlockWithNumberParentHashAndTxInfo(5, HashOfLastBlockIn(blockTreeSetup),
                dupGasPriceGroup);
            blockTreeSetup = new BlockTreeSetup(new[]{dupGasPriceBlock},true);

            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup.EthRpcModule.eth_gasPrice();

            resultWrapper.Result.Should().Be(Result.Success);
        }
        
        [Test]
        public void Eth_gasPrice_BlockcountEqualToBlocksToCheck_ShouldGetSixtiethPercentileIndex()
        {
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup();
            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup.EthRpcModule.eth_gasPrice();
            resultWrapper.Data.Should().Be((UInt256?) 4); 
            //Tx Gas Prices: 1,2,3,4,5,6, Index: (6-1) * 3/5 = 3, Gas Price: 4
        }

        [Test]
        public void Eth_gasPrice_EstimatedGasPriceMoreThanMaxGasPrice_ReturnMaxGasPrice()
        {
            Block[] blockArray = GetBlocksFromKeyValuePairs(
                BlockNumberAndTxStringsKeyValuePair(0, CollectTxStrings(
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
            Block[] blockArray = GetBlocksFromKeyValuePairs(
                BlockNumberAndTxStringsKeyValuePair(0, CollectTxStrings(
                    GetTxString("A", "4", "0"),
                    GetTxString("B", "3", "0"),
                    GetTxString("C", "2", "0"),
                    GetTxString("D", "1", "0")
                    )
                ));
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(blockArray);

            blockTreeSetup.EthRpcModule.eth_gasPrice();
            
            List<UInt256> gasPriceList = blockTreeSetup.GasPriceOracle.TxGasPriceList;
            gasPriceList.Count.Should().Be(3);
        }
        
       [Test] 
        public void Eth_gas_price_BlocksWithMoreThanThreeTxs_OnlyAddsThreeLowestEffectiveGasPriceTxs()
        {
            Block[] blockArray = GetBlocksFromKeyValuePairs(
                BlockNumberAndTxStringsKeyValuePair(0, CollectTxStrings(
                    GetTxString("A", "4", "0"),
                    GetTxString("B", "3", "0"),
                    GetTxString("C", "2", "0"),
                    GetTxString("D", "1", "0")
                    )
                ),
                BlockNumberAndTxStringsKeyValuePair(1, CollectTxStrings(
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
            Block[] blocks = GetBlocksFromKeyValuePairs(
                    BlockNumberAndTxStringsKeyValuePair(0, null), 
                    BlockNumberAndTxStringsKeyValuePair(1, null),
                    BlockNumberAndTxStringsKeyValuePair(2, null),
                    BlockNumberAndTxStringsKeyValuePair(3, null),
                    BlockNumberAndTxStringsKeyValuePair(4, null));
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(blocks);
            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup.EthRpcModule.eth_gasPrice();
            
            resultWrapper.Data.Should().Be((UInt256?) 1);
        }
        
        [Test]
        public void Eth_gasPrice_ReturnDefaultGasPrice_EmptyBlocksAtEndEqualToEight()
        {
            Block[] blocks = GetBlocksFromKeyValuePairs(
                BlockNumberAndTxStringsKeyValuePair(0, CollectTxStrings(
                        GetTxString("A", "2", "0"),
                        GetTxString("B", "3", "0")
                        )
                ),
                BlockNumberAndTxStringsKeyValuePair(1, null),
                BlockNumberAndTxStringsKeyValuePair(2, null),
                BlockNumberAndTxStringsKeyValuePair(3, null),
                BlockNumberAndTxStringsKeyValuePair(4, null),
                BlockNumberAndTxStringsKeyValuePair(5, null),
                BlockNumberAndTxStringsKeyValuePair(6, null),
                BlockNumberAndTxStringsKeyValuePair(7, null),
                BlockNumberAndTxStringsKeyValuePair(8, null)
                );

            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(blocks);
            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup.EthRpcModule.eth_gasPrice();
            
            resultWrapper.Data.Should().Be((UInt256?) 1); 
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
        
        [Test]
        public void Eth_gasPrice_TxGasPricesAreBelowThreshold_ReplaceGasPriceUnderThresholdWithDefaultPrice()
        {
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(ignoreUnder: 4);
            blockTreeSetup.EthRpcModule.eth_gasPrice();
            
            List<UInt256> expected = new List<UInt256> {1,1,4,5,6};
            blockTreeSetup.GasPriceOracle.TxGasPriceList.Should().Equal(expected); 
        }

        [TestCase(false, 5)] //Tx Gas Prices: 1,1,2,3,4,5,6,9,10,11 Index: (10-1) * 3/5 rounds to 5, Gas Price: 5
        [TestCase(true, 5)]  //Tx Gas Prices: 0,0,1,2,3,4,5,6,9,10,11 Index: (11-1) * 3/5 = 6, Gas Price: 5
        public void Eth_gasPrice_InEip1559Mode_ShouldCalculateTxGasPricesDifferently(bool eip1559Enabled, int expected)
        {
            Transaction[] eip1559TxGroup =  GetTransactionsFromTxStrings(CollectTxStrings(
                    GetTxString("B","7","2"), 
                    GetTxString("B","8","3")
                    ), IsEip1559()
                );
            Transaction[] notEip1559TxGroup = GetTransactionsFromTxStrings(CollectTxStrings(
                    GetTxString("B","9","4"),
                    GetTxString("B","10","5"),
                    GetTxString("B","11","6")
                    ), IsNotEip1559()
                );
            
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup();
            Block eip1559Block = GetBlockWithNumberParentHashAndTxInfo(5, HashOfLastBlockIn(blockTreeSetup), eip1559TxGroup);
            Block nonEip1559Block = GetBlockWithNumberParentHashAndTxInfo(6, eip1559Block.Hash, notEip1559TxGroup);
            blockTreeSetup = new BlockTreeSetup(new Block[]{eip1559Block, nonEip1559Block},true, 
                eip1559Enabled: eip1559Enabled);

            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup.EthRpcModule.eth_gasPrice();
            
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
            return blockTreeSetup.Blocks[^1].Hash;
        }


        [Test]
        public void Eth_gasPrice_TxCountNotGreaterThanLimit_GetTxFromMoreBlocks()
        {
            Block[] blocks = GetBlocksFromKeyValuePairs(
                BlockNumberAndTxStringsKeyValuePair(0, CollectTxStrings(
                        GetTxString("A", "0", "0"),
                        GetTxString("B", "1", "0")
                    )
                ),
                BlockNumberAndTxStringsKeyValuePair(1, CollectTxStrings(
                        GetTxString("C", "2", "0"),
                        GetTxString("D", "3","0")
                    )
                ),
                BlockNumberAndTxStringsKeyValuePair(2, CollectTxStrings(
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
            Block[] blocks = GetBlocksFromKeyValuePairs(
                BlockNumberAndTxStringsKeyValuePair(0, CollectTxStrings(
                        GetTxString("A", "3", "0"),
                        GetTxString("B", "4", "0")
                    )
                ),
                BlockNumberAndTxStringsKeyValuePair(1, CollectTxStrings(
                        GetTxString("C", "5", "0"),
                        GetTxString("D", "6","0")
                    )
                ),
                BlockNumberAndTxStringsKeyValuePair(2, CollectTxStrings(
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
            Block[] blocks = GetBlocksFromKeyValuePairs(
                BlockNumberAndTxStringsKeyValuePair(0, CollectTxStrings(
                    GetTxString("A", "1", "0"),
                    GetTxString("B", "2", "0")
                    )
                ),
                BlockNumberAndTxStringsKeyValuePair(1, CollectTxStrings(
                    GetTxString("C", "3", "0"),
                    GetTxString("D", "4", "0")
                    )
                ),
                BlockNumberAndTxStringsKeyValuePair(2, CollectTxStrings(
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
        
        private Block GetBlockWithBeneficiaryBlockNumberAndTxInfo(Address beneficiary, int blockNumber, string[][] txInfo)
        {
            Transaction[] transactions = GetTransactionsFromTxStrings(txInfo, false);
            return Build.A.Block.WithBeneficiary(beneficiary).WithNumber(blockNumber).WithTransactions(transactions)
                .TestObject;
        }
        
        private string[][] CollectTxStrings(params string[][] txInfo)
        {
            return txInfo;
        }

        private string[] GetTxString(string privateKeyLetter, string gasPrice, string nonce)
        {
            return new[] {privateKeyLetter, gasPrice, nonce};
        }

        private KeyValuePair<int, string[][]> BlockNumberAndTxStringsKeyValuePair(int blockNumber, string[][] txInfo)
        {
            return new KeyValuePair<int, string[][]>(blockNumber, txInfo);
        }
        
        
        private Block[] GetBlocksFromKeyValuePairs(params KeyValuePair<int, string[][]>[] blockAndTxInfo)
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
            transactions = GetTransactionsFromTxStrings(txInfoArray, isEip1559);
            block = GetBlockWithNumberParentHashAndTxInfo(blockNumber, parentHash, transactions);
            return block;
        }

        private Transaction[] GetTransactionsFromTxStrings(string[][] txInfo, bool isEip1559)
        {
            if (txInfo == null)
            {
                return Array.Empty<Transaction>();
            }
            else if (isEip1559 == true)
            {
                return ConvertEip1559Txs(txInfo).ToArray();
            }
            else
            {
                return ConvertRegularTxs(txInfo).ToArray();
            }
        }

        private IEnumerable<Transaction> ConvertEip1559Txs(params string[][] txsInfo)
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

        private Transaction[] ConvertRegularTxs(params string[][] txsInfo)
        {
            PrivateKey privateKey;
            char privateKeyLetter;
            UInt256 gasPrice;
            UInt256 nonce;
            Transaction transaction;
            List<Transaction> transactions = new List<Transaction>();
            foreach (string[] txInfo in txsInfo)
            {
                privateKeyLetter = Convert.ToChar(txInfo[0]);
                privateKey = PrivateKeyForLetter(privateKeyLetter);
                gasPrice = UInt256.Parse(txInfo[1]);
                nonce = UInt256.Parse(txInfo[2]);
                transaction = Build.A.Transaction.SignedAndResolved(privateKey).WithGasPrice(gasPrice).WithNonce(nonce)
                    .TestObject;
                transactions.Add(transaction);
            }

            return transactions.ToArray();
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

            public BlockTreeSetup(
                Block[] blocks = null,
                bool addBlocks = false,
                IGasPriceOracle? gasPriceOracle = null, 
                int? blockLimit = null,
                UInt256? ignoreUnder = null,
                UInt256? baseFee = null,
                bool eip1559Enabled = false)
            {
                GetBlocks(blocks, addBlocks);

                InitializeAndAddToBlockTree();

                GasPriceOracle = gasPriceOracle ?? GetGasPriceOracle(eip1559Enabled, ignoreUnder, blockLimit, baseFee);
                
                GetEthRpcModule();
            }

            private void InitializeAndAddToBlockTree()
            {
                BlockTree = BuildABlockTreeWithGenesisBlock(Blocks[0]);
                foreach (Block block in Blocks)
                {
                    BlockTreeBuilder.AddBlock(BlockTree, block);
                }
            }

            private void GetBlocks(Block[] blocks, bool shouldAddToBlocks)
            {
                if (NoBlocksGiven(blocks) || shouldAddToBlocks)
                {
                    GetBlockArray();
                    if (shouldAddToBlocks)
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
                Blocks = e.GetBlocksFromKeyValuePairs(
                    e.BlockNumberAndTxStringsKeyValuePair(0, e.CollectTxStrings(
                            e.GetTxString("A", "1", "0"),
                            e.GetTxString("B", "2", "0")
                        )
                    ),
                    e.BlockNumberAndTxStringsKeyValuePair(1, e.CollectTxStrings(
                            e.GetTxString("C", "3", "0")
                        )
                    ),
                    e.BlockNumberAndTxStringsKeyValuePair(2, e.CollectTxStrings(
                            e.GetTxString("D", "5", "0")
                        )
                    ),
                    e.BlockNumberAndTxStringsKeyValuePair(3, e.CollectTxStrings(
                            e.GetTxString("A", "4", "1")
                        )
                    ),
                    e.BlockNumberAndTxStringsKeyValuePair(4, e.CollectTxStrings(
                            e.GetTxString("B", "6", "1")
                        )
                    )
                );
            }

            private void GetEthRpcModule()
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
