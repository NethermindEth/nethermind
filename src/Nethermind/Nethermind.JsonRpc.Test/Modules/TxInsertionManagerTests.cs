using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules
{
    class TestableTxInsertionManager : TxInsertionManager
    {
        public readonly List<UInt256> _txGasPriceList;
        public TestableTxInsertionManager(
            IGasPriceOracle gasPriceOracle,
            UInt256? ignoreUnder,
            UInt256 baseFee,
            bool isEip1559Enabled,
            List<UInt256> txGasPriceList = null) : 
            base(gasPriceOracle,
                ignoreUnder,
                baseFee,
                isEip1559Enabled)
        {
            _txGasPriceList = txGasPriceList ?? new List<UInt256>();
        }

        protected override List<UInt256> GetTxGasPriceList()
        {
            return _txGasPriceList;
        }
    }
    [TestFixture]
    class TxInsertionManagerTests
    {
        [Test]
        public void AddValidTxAndReturnCount_IfBlockHasMoreThanThreeValidTx_AddOnlyThree()
        {
            EthRpcModuleTests e = new EthRpcModuleTests();
            TestableTxInsertionManager testableTxInsertionManager = GetTestableTxInsertionManager(ignoreUnder: 3);
            Block[] blocks = e.GetTwoTestBlocks();
            
            testableTxInsertionManager.AddValidTxAndReturnCount(blocks[1]);

            testableTxInsertionManager._txGasPriceList.Count.Should().Be(3);
        }

        [Test]
        public void AddValidTxAndReturnCount_IfBlockHasMoreThanThreeValidTxs_OnlyAddTxsWithLowestGasPrices()
        {
            EthRpcModuleTests e = new EthRpcModuleTests();
            TestableTxInsertionManager testableTxInsertionManager = GetTestableTxInsertionManager(ignoreUnder: 3);
            Block[] blocks = e.GetTwoTestBlocks();
            List<UInt256> expected = new List<UInt256> {5,6,7};
            
            testableTxInsertionManager.AddValidTxAndReturnCount(blocks[1]);

            testableTxInsertionManager._txGasPriceList.Should().BeEquivalentTo(expected);
        }
        
        [Test]
        public void AddValidTxAndReturnCount_TxsWithPriceLessThanIgnoreUnder_ShouldNotHaveGasPriceInTxGasPriceList()
        {
            EthRpcModuleTests e = new EthRpcModuleTests();
            TestableTxInsertionManager testableTxInsertionManager = GetTestableTxInsertionManager(ignoreUnder: 3);
            Block[] blocks = e.GetTwoTestBlocks();
            List<UInt256> expected = new List<UInt256> {3, 4};
            
            testableTxInsertionManager.AddValidTxAndReturnCount(blocks[0]);

            testableTxInsertionManager._txGasPriceList.Should().BeEquivalentTo(expected);
        }
        
        [Test]
        public void AddValidTxAndReturnCount_TxsSentByMiner_ShouldNotHaveGasPriceInTxGasPriceList()
        {
                
        }

        [Test]
        public void AddValidTxAndReturnCount_WhenEip1559Enabled_EffectiveGasPriceOfTxsCalculated()
        {
            
        }
        
        [Test]
        public void AddValidTxAndReturnCount_IfNoValidTxsInABlock_DefaultPriceAddedToListInstead()
        {
            
        }

        private TestableTxInsertionManager GetTestableTxInsertionManager(
            IGasPriceOracle gasPriceOracle = null,
            UInt256? ignoreUnder = null,
            UInt256? baseFee = null,
            bool isEip1559Enabled = false,
            List<UInt256> txGasPriceList = null)
        {
            return new TestableTxInsertionManager(
                gasPriceOracle ?? Substitute.For<IGasPriceOracle>(),
                ignoreUnder ?? UInt256.Zero,
                baseFee ?? UInt256.Zero,
                isEip1559Enabled,
                txGasPriceList);
        }
    }
}
