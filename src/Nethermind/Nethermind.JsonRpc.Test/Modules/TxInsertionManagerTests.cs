using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.JsonRpc.Test.Modules.EthRpcModuleTests;
using static Nethermind.JsonRpc.Test.Modules.BlockConstructor;

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
            
            testableTxInsertionManager.AddValidTxFromBlockAndReturnCount(blocks[1]);

            testableTxInsertionManager._txGasPriceList.Count.Should().Be(3);
        }

        [Test]
        public void AddValidTxAndReturnCount_IfBlockHasMoreThanThreeValidTxs_OnlyAddTxsWithLowestGasPrices()
        {
            EthRpcModuleTests e = new EthRpcModuleTests();
            TestableTxInsertionManager testableTxInsertionManager = GetTestableTxInsertionManager(ignoreUnder: 3);
            Block[] blocks = e.GetTwoTestBlocks();
            List<UInt256> expected = new List<UInt256> {5,6,7};
            
            testableTxInsertionManager.AddValidTxFromBlockAndReturnCount(blocks[1]);

            testableTxInsertionManager._txGasPriceList.Should().BeEquivalentTo(expected);
        }
        
        [Test]
        public void AddValidTxAndReturnCount_TxsWithPriceLessThanIgnoreUnder_ShouldNotHaveGasPriceInTxGasPriceList()
        {
            EthRpcModuleTests e = new EthRpcModuleTests();
            TestableTxInsertionManager testableTxInsertionManager = GetTestableTxInsertionManager(ignoreUnder: 3);
            Block[] blocks = e.GetTwoTestBlocks();
            List<UInt256> expected = new List<UInt256> {3, 4};
            
            testableTxInsertionManager.AddValidTxFromBlockAndReturnCount(blocks[0]);

            testableTxInsertionManager._txGasPriceList.Should().BeEquivalentTo(expected);
        }
        
        [Test]
        public void AddValidTxAndReturnCount_TxsSentByMiner_ShouldNotHaveGasPriceInTxGasPriceList()
        {
            Address minerAddress = PrivateKeyForLetter('A').Address;
            Transaction[] transactions =
            {
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).WithGasPrice(7).TestObject,
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyB).WithGasPrice(8).TestObject,
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyC).WithGasPrice(9).TestObject,
            };
            Block block = Build.A.Block.WithBeneficiary(minerAddress).WithTransactions(transactions).WithNumber(0)
                .TestObject;
            TestableTxInsertionManager testableTxInsertionManager = GetTestableTxInsertionManager();
            List<UInt256> expected = new List<UInt256>{8,9};

            testableTxInsertionManager.AddValidTxFromBlockAndReturnCount(block);
            
            testableTxInsertionManager._txGasPriceList.Should().BeEquivalentTo(expected);
        }
        
        [Test]
        public void AddValidTxAndReturnCount_WhenEip1559Enabled_EffectiveGasPriceOfTxsCalculated()
        {
            Transaction[] eip1559TxGroup =
            {
                Build.A.Transaction.WithFeeCap(27).WithGasPrice(1).WithType(TxType.EIP1559).TestObject, //Min(27, 1 + 25) => 26
                Build.A.Transaction.WithFeeCap(27).WithGasPrice(2).WithType(TxType.EIP1559).TestObject, //Min(27, 2 + 25) => 27
                Build.A.Transaction.WithFeeCap(27).WithGasPrice(3).WithType(TxType.EIP1559).TestObject, //Min(27, 3 + 25) => 27
            };
            Transaction[] nonEip1559TxGroup =
            {
                Build.A.Transaction.WithFeeCap(27).WithGasPrice(9).TestObject, //9
                Build.A.Transaction.WithFeeCap(27).WithGasPrice(10).TestObject, //10
                Build.A.Transaction.WithFeeCap(27).WithGasPrice(11).TestObject, //11
            };
            TestableTxInsertionManager testableTxInsertionManager = GetTestableTxInsertionManager(baseFee:25, isEip1559Enabled: true);
            Block eip1559Block = GetBlockWithNumberParentHashAndTxInfo(0, Keccak.Zero, eip1559TxGroup);
            Block nonEip1559Block = GetBlockWithNumberParentHashAndTxInfo(1, eip1559Block.Hash, nonEip1559TxGroup);
            List<UInt256> expected = new List<UInt256> {26, 27, 27, 9, 10, 11};
            
            testableTxInsertionManager.AddValidTxFromBlockAndReturnCount(eip1559Block);
            testableTxInsertionManager.AddValidTxFromBlockAndReturnCount(nonEip1559Block);
            
            testableTxInsertionManager._txGasPriceList.Should().BeEquivalentTo(expected);
        }
        
        [Test]
        public void AddValidTxAndReturnCount_IfNoValidTxsInABlock_DefaultPriceAddedToListInstead()
        {
            Transaction[] transactions =
            {
                Build.A.Transaction.WithGasPrice(1).TestObject, 
                Build.A.Transaction.WithGasPrice(2).TestObject,
                Build.A.Transaction.WithGasPrice(3).TestObject
            };
            Block block = Build.A.Block.Genesis.WithTransactions(transactions).TestObject;
            IGasPriceOracle gasPriceOracle = Substitute.For<IGasPriceOracle>();
            gasPriceOracle.FallbackGasPrice.Returns((UInt256?) 3);
            TestableTxInsertionManager testableTxInsertionManager = GetTestableTxInsertionManager(gasPriceOracle:gasPriceOracle, ignoreUnder: 4);
            List<UInt256> expected = new List<UInt256> {3};

            testableTxInsertionManager.AddValidTxFromBlockAndReturnCount(block);
            
            testableTxInsertionManager._txGasPriceList.Should().Equal(expected); 
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
