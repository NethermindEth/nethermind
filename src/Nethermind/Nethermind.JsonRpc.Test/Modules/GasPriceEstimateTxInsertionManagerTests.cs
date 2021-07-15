using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.JsonRpc.Test.Modules.TestBlockConstructor;

namespace Nethermind.JsonRpc.Test.Modules
{
    [TestFixture]
    class GasPriceEstimateTxInsertionManagerTests
    {
        [Test]
        public void AddValidTxAndReturnCount_IfBlockHasMoreThanThreeValidTx_AddOnlyThree()
        {
            TestableGasPriceEstimateTxInsertionManager testableGasPriceEstimateTxInsertionManager = GetTestableTxInsertionManager(ignoreUnder: 3);
            Block testBlock = GetTestBlockB();
            
            testableGasPriceEstimateTxInsertionManager.AddValidTxFromBlockAndReturnCount(testBlock);

            testableGasPriceEstimateTxInsertionManager._txGasPriceList.Count.Should().Be(3);
        }

        [Test]
        public void AddValidTxAndReturnCount_IfBlockHasMoreThanThreeValidTxs_OnlyAddTxsWithLowestGasPrices()
        {
            TestableGasPriceEstimateTxInsertionManager testableGasPriceEstimateTxInsertionManager = GetTestableTxInsertionManager(ignoreUnder: 3);
            Block testBlock = GetTestBlockB();
            List<UInt256> expected = new List<UInt256> {5,6,7};
            
            testableGasPriceEstimateTxInsertionManager.AddValidTxFromBlockAndReturnCount(testBlock);

            testableGasPriceEstimateTxInsertionManager._txGasPriceList.Should().BeEquivalentTo(expected);
        }
        
        [Test]
        public void AddValidTxAndReturnCount_TxsWithPriceLessThanIgnoreUnder_ShouldNotHaveGasPriceInTxGasPriceList()
        {
            Block testBlock = GetTestBlockA();
            TestableGasPriceEstimateTxInsertionManager testableGasPriceEstimateTxInsertionManager = GetTestableTxInsertionManager(ignoreUnder: 3);
            List<UInt256> expected = new List<UInt256> {3, 4};
            
            testableGasPriceEstimateTxInsertionManager.AddValidTxFromBlockAndReturnCount(testBlock);

            testableGasPriceEstimateTxInsertionManager._txGasPriceList.Should().BeEquivalentTo(expected);
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
            TestableGasPriceEstimateTxInsertionManager testableGasPriceEstimateTxInsertionManager = GetTestableTxInsertionManager();
            List<UInt256> expected = new List<UInt256>{8,9};
            
            testableGasPriceEstimateTxInsertionManager.AddValidTxFromBlockAndReturnCount(block);
            
            testableGasPriceEstimateTxInsertionManager._txGasPriceList.Should().BeEquivalentTo(expected);
        }
        
        [Test]
        public void AddValidTxAndReturnCount_WhenEip1559Enabled_EffectiveGasPriceOfTxsCalculated()
        {
            Transaction[] eip1559TxGroup = GetEip1559TxGroup();
            Transaction[] nonEip1559TxGroup = GetNonEip1559TxGroup();

            Block eip1559Block = Build.A.Block.Genesis.WithTransactions(eip1559TxGroup).TestObject;
            Block nonEip1559Block = Build.A.Block.WithNumber(1).WithParentHash(eip1559Block.Hash).
                WithTransactions(nonEip1559TxGroup).TestObject;
            ISpecProvider specProvider = VariableReturnMockSpecProvider();
            TestableGasPriceEstimateTxInsertionManager testableGasPriceEstimateTxInsertionManager =
                GasPriceEstimateTxInsertionManagerWith(specProvider);
            List<UInt256> expected = new List<UInt256> {26, 27, 27, 9, 10, 11};
            
            testableGasPriceEstimateTxInsertionManager.AddValidTxFromBlockAndReturnCount(eip1559Block);
            testableGasPriceEstimateTxInsertionManager.AddValidTxFromBlockAndReturnCount(nonEip1559Block);
            
            testableGasPriceEstimateTxInsertionManager._txGasPriceList.Should().BeEquivalentTo(expected);

            Transaction[] GetEip1559TxGroup()
            {
                return new[]
                {
                    Build.A.Transaction.WithMaxFeePerGas(27).WithGasPrice(1).WithType(TxType.EIP1559).TestObject, //Min(27, 1 + 25) => 26
                    Build.A.Transaction.WithMaxFeePerGas(27).WithGasPrice(2).WithType(TxType.EIP1559).TestObject, //Min(27, 2 + 25) => 27
                    Build.A.Transaction.WithMaxFeePerGas(27).WithGasPrice(3).WithType(TxType.EIP1559).TestObject //Min(27, 3 + 25) => 27
                };
            }

            Transaction[] GetNonEip1559TxGroup()
            {
                return new[]
                {
                    Build.A.Transaction.WithMaxFeePerGas(27).WithGasPrice(9).TestObject, //Min(9, 9 + 25) => 9
                    Build.A.Transaction.WithMaxFeePerGas(27).WithGasPrice(10).TestObject, //Min(10, 10 + 25) => 10
                    Build.A.Transaction.WithMaxFeePerGas(27).WithGasPrice(11).TestObject //Min(11, 11 + 25) => 11
                };
            }
            static ISpecProvider VariableReturnMockSpecProvider()
            {
                ISpecProvider specProvider = Substitute.For<ISpecProvider>();
                
                IReleaseSpec trueEip1559 = Substitute.For<IReleaseSpec>();
                trueEip1559.IsEip1559Enabled.Returns(true);
                IReleaseSpec falseEip1559 = Substitute.For<IReleaseSpec>();
                falseEip1559.IsEip1559Enabled.Returns(false);
                
                specProvider.GetSpec(Arg.Is<long>(b => b == 0)).Returns(trueEip1559);
                specProvider.GetSpec(Arg.Is<long>(b => b == 1)).Returns(falseEip1559);
                
                return specProvider;
            }

            TestableGasPriceEstimateTxInsertionManager GasPriceEstimateTxInsertionManagerWith(ISpecProvider specProvider1)
            {
                IGasPriceOracle gasPriceOracle = Substitute.For<IGasPriceOracle>();
                gasPriceOracle.FallbackGasPrice.Returns(UInt256.One);
               
                TestableGasPriceEstimateTxInsertionManager testableGasPriceEstimateTxInsertionManager =
                    GetTestableTxInsertionManager(baseFee: 25, specProvider: specProvider1, gasPriceOracle: gasPriceOracle);
                
                return testableGasPriceEstimateTxInsertionManager;
            }

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
            TestableGasPriceEstimateTxInsertionManager testableGasPriceEstimateTxInsertionManager = GetTestableTxInsertionManager(gasPriceOracle:gasPriceOracle, ignoreUnder: 4);
            List<UInt256> expected = new List<UInt256> {7};

            testableGasPriceEstimateTxInsertionManager.AddValidTxFromBlockAndReturnCount(block);
            
            testableGasPriceEstimateTxInsertionManager._txGasPriceList.Should().Equal(expected); 
        }

        [Test]
        public void AddValidTxAndReturnCount_IfNoTxsInABlock_DefaultPriceAddedToListInstead()
        {
            Transaction[] transactions = Array.Empty<Transaction>();
            Block block = Build.A.Block.Genesis.WithTransactions(transactions).TestObject;
            IGasPriceOracle gasPriceOracle = Substitute.For<IGasPriceOracle>();
            gasPriceOracle.FallbackGasPrice.Returns((UInt256?) 3);
            TestableGasPriceEstimateTxInsertionManager testableGasPriceEstimateTxInsertionManager = GetTestableTxInsertionManager(gasPriceOracle:gasPriceOracle);
            List<UInt256> expected = new List<UInt256> {3};

            testableGasPriceEstimateTxInsertionManager.AddValidTxFromBlockAndReturnCount(block);
            
            testableGasPriceEstimateTxInsertionManager._txGasPriceList.Should().Equal(expected); 
        }
        
    class TestableGasPriceEstimateTxInsertionManager : GasPriceEstimateTxInsertionManager
    {
        public readonly List<UInt256> _txGasPriceList;
        public TestableGasPriceEstimateTxInsertionManager(
            IGasPriceOracle gasPriceOracle,
            UInt256? ignoreUnder,
            UInt256 baseFee,
            ISpecProvider specProvider,
            List<UInt256> txGasPriceList = null) : 
            base(gasPriceOracle,
                ignoreUnder,
                baseFee,
                specProvider)
        {
            _txGasPriceList = txGasPriceList ?? new List<UInt256>();
        }

        protected override List<UInt256> GetTxGasPriceList()
        {
            return _txGasPriceList;
        }
    }
        private TestableGasPriceEstimateTxInsertionManager GetTestableTxInsertionManager(
            IGasPriceOracle gasPriceOracle = null,
            UInt256? ignoreUnder = null,
            UInt256? baseFee = null,
            ISpecProvider specProvider = null,
            List<UInt256> txGasPriceList = null)
        {
            return new(
                gasPriceOracle ?? Substitute.For<IGasPriceOracle>(),
                ignoreUnder ?? UInt256.Zero,
                baseFee ?? UInt256.Zero,
                specProvider ?? Substitute.For<ISpecProvider>(),
                txGasPriceList);
        }
    }
}
