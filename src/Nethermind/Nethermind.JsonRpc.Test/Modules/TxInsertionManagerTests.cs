using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.JsonRpc.Test.Modules.TestBlockConstructor;

namespace Nethermind.JsonRpc.Test.Modules
{
    [TestFixture]
    class TxInsertionManagerTests
    {
        [Test]
        public void AddValidTxAndReturnCount_IfBlockHasMoreThanThreeValidTx_AddOnlyThree()
        {
            TestableGasPriceEstimateGasPriceEstimateTxInsertionManager testableGasPriceEstimateGasPriceEstimateTxInsertionManager = GetTestableTxInsertionManager(ignoreUnder: 3);
            Block testBlock = GetTestBlockB();
            
            testableGasPriceEstimateGasPriceEstimateTxInsertionManager.AddValidTxFromBlockAndReturnCount(testBlock);

            testableGasPriceEstimateGasPriceEstimateTxInsertionManager._txGasPriceList.Count.Should().Be(3);
        }

        [Test]
        public void AddValidTxAndReturnCount_IfBlockHasMoreThanThreeValidTxs_OnlyAddTxsWithLowestGasPrices()
        {
            TestableGasPriceEstimateGasPriceEstimateTxInsertionManager testableGasPriceEstimateGasPriceEstimateTxInsertionManager = GetTestableTxInsertionManager(ignoreUnder: 3);
            Block testBlock = GetTestBlockB();
            List<UInt256> expected = new List<UInt256> {5,6,7};
            
            testableGasPriceEstimateGasPriceEstimateTxInsertionManager.AddValidTxFromBlockAndReturnCount(testBlock);

            testableGasPriceEstimateGasPriceEstimateTxInsertionManager._txGasPriceList.Should().BeEquivalentTo(expected);
        }
        
        [Test]
        public void AddValidTxAndReturnCount_TxsWithPriceLessThanIgnoreUnder_ShouldNotHaveGasPriceInTxGasPriceList()
        {
            Block testBlock = GetTestBlockA();
            TestableGasPriceEstimateGasPriceEstimateTxInsertionManager testableGasPriceEstimateGasPriceEstimateTxInsertionManager = GetTestableTxInsertionManager(ignoreUnder: 3);
            List<UInt256> expected = new List<UInt256> {3, 4};
            
            testableGasPriceEstimateGasPriceEstimateTxInsertionManager.AddValidTxFromBlockAndReturnCount(testBlock);

            testableGasPriceEstimateGasPriceEstimateTxInsertionManager._txGasPriceList.Should().BeEquivalentTo(expected);
        }
        
        [Test]
        public void AddValidTxAndReturnCount_TxsSentByMiner_ShouldNotHaveGasPriceInTxGasPriceList()
        {
            Address minerAddress = TestItem.PrivateKeyA.Address;
            Transaction[] transactions =
            {
                Build.A.Transaction.WithGasPrice(7).SignedAndResolved(TestItem.PrivateKeyA).WithNonce(0).TestObject,
                Build.A.Transaction.WithGasPrice(8).SignedAndResolved(TestItem.PrivateKeyB).WithNonce(0).TestObject,
                Build.A.Transaction.WithGasPrice(9).SignedAndResolved(TestItem.PrivateKeyC).WithNonce(0).TestObject,
            };
            Block block = Build.A.Block.Genesis.WithBeneficiary(minerAddress).WithTransactions(transactions).TestObject;
            TestableGasPriceEstimateGasPriceEstimateTxInsertionManager testableGasPriceEstimateGasPriceEstimateTxInsertionManager = GetTestableTxInsertionManager();
            List<UInt256> expected = new List<UInt256>{8,9};
            
            testableGasPriceEstimateGasPriceEstimateTxInsertionManager.AddValidTxFromBlockAndReturnCount(block);
            
            testableGasPriceEstimateGasPriceEstimateTxInsertionManager._txGasPriceList.Should().BeEquivalentTo(expected);
        }
        
        [Test]
        public void AddValidTxAndReturnCount_WhenEip1559Enabled_EffectiveGasPriceOfTxsCalculated()
        {
            Transaction[] eip1559TxGroup =
            {
                Build.A.Transaction.WithFeeCap(27).WithGasPrice(1).WithType(TxType.EIP1559).TestObject, //Min(27, 1 + 25) => 26
                Build.A.Transaction.WithFeeCap(27).WithGasPrice(2).WithType(TxType.EIP1559).TestObject, //Min(27, 2 + 25) => 27
                Build.A.Transaction.WithFeeCap(27).WithGasPrice(3).WithType(TxType.EIP1559).TestObject  //Min(27, 3 + 25) => 27
            };
            
            Transaction[] nonEip1559TxGroup =
            {
                Build.A.Transaction.WithFeeCap(27).WithGasPrice(9).TestObject,  //Min(9, 9 + 25) => 9
                Build.A.Transaction.WithFeeCap(27).WithGasPrice(10).TestObject, //Min(10, 10 + 25) => 10
                Build.A.Transaction.WithFeeCap(27).WithGasPrice(11).TestObject  //Min(11, 11 + 25) => 11
            };

            Block eip1559Block = Build.A.Block.WithNumber(1).WithParentHash(Keccak.Zero).WithTransactions(eip1559TxGroup).TestObject;
            Block nonEip1559Block = Build.A.Block.WithNumber(1).WithParentHash(eip1559Block.Hash).WithTransactions(nonEip1559TxGroup).TestObject;
            TestableGasPriceEstimateGasPriceEstimateTxInsertionManager testableGasPriceEstimateGasPriceEstimateTxInsertionManager = GetTestableTxInsertionManager(baseFee:25, isEip1559Enabled: true);
            List<UInt256> expected = new List<UInt256> {26, 27, 27, 9, 10, 11};
            
            testableGasPriceEstimateGasPriceEstimateTxInsertionManager.AddValidTxFromBlockAndReturnCount(eip1559Block);
            testableGasPriceEstimateGasPriceEstimateTxInsertionManager.AddValidTxFromBlockAndReturnCount(nonEip1559Block);
            
            testableGasPriceEstimateGasPriceEstimateTxInsertionManager._txGasPriceList.Should().BeEquivalentTo(expected);
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
            gasPriceOracle.FallbackGasPrice.Returns((UInt256?) 7);
            TestableGasPriceEstimateGasPriceEstimateTxInsertionManager testableGasPriceEstimateGasPriceEstimateTxInsertionManager = GetTestableTxInsertionManager(gasPriceOracle:gasPriceOracle, ignoreUnder: 4);
            List<UInt256> expected = new List<UInt256> {7};

            testableGasPriceEstimateGasPriceEstimateTxInsertionManager.AddValidTxFromBlockAndReturnCount(block);
            
            testableGasPriceEstimateGasPriceEstimateTxInsertionManager._txGasPriceList.Should().Equal(expected); 
        }

        [Test]
        public void AddValidTxAndReturnCount_IfNoTxsInABlock_DefaultPriceAddedToListInstead()
        {
            Transaction[] transactions = Array.Empty<Transaction>();
            Block block = Build.A.Block.Genesis.WithTransactions(transactions).TestObject;
            IGasPriceOracle gasPriceOracle = Substitute.For<IGasPriceOracle>();
            gasPriceOracle.FallbackGasPrice.Returns((UInt256?) 3);
            TestableGasPriceEstimateGasPriceEstimateTxInsertionManager testableGasPriceEstimateGasPriceEstimateTxInsertionManager = GetTestableTxInsertionManager(gasPriceOracle:gasPriceOracle);
            List<UInt256> expected = new List<UInt256> {3};

            testableGasPriceEstimateGasPriceEstimateTxInsertionManager.AddValidTxFromBlockAndReturnCount(block);
            
            testableGasPriceEstimateGasPriceEstimateTxInsertionManager._txGasPriceList.Should().Equal(expected); 
        }
        
    class TestableGasPriceEstimateGasPriceEstimateTxInsertionManager : GasPriceEstimateGasPriceEstimateTxInsertionManager
    {
        public readonly List<UInt256> _txGasPriceList;
        public TestableGasPriceEstimateGasPriceEstimateTxInsertionManager(
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
        private TestableGasPriceEstimateGasPriceEstimateTxInsertionManager GetTestableTxInsertionManager(
            IGasPriceOracle gasPriceOracle = null,
            UInt256? ignoreUnder = null,
            UInt256? baseFee = null,
            bool isEip1559Enabled = false,
            List<UInt256> txGasPriceList = null)
        {
            return new TestableGasPriceEstimateGasPriceEstimateTxInsertionManager(
                gasPriceOracle ?? Substitute.For<IGasPriceOracle>(),
                ignoreUnder ?? UInt256.Zero,
                baseFee ?? UInt256.Zero,
                isEip1559Enabled,
                txGasPriceList);
        }
    }
}
