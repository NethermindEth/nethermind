using System.Collections.Generic;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.JsonRpc.Test.Modules.EthRpcModuleTests.GasPriceTest;

namespace Nethermind.JsonRpc.Test.Modules
{
    [TestFixture]
    class GasPriceOracleTests
    {
        [Test]
        public void GasPriceEstimate_ChangeInHeadBlock_ReturnsPreviousGasPrice()
        {
            IHeadBlockChangeManager headBlockChangeManager = Substitute.For<IHeadBlockChangeManager>();
            GasPriceOracle gasPriceOracle = GetGasPriceOracle(headBlockChangeManager: headBlockChangeManager);
            IDictionary<long, Block> testDictionary = Substitute.For<IDictionary<long, Block>>();
            Block initialHead = Build.A.Block.WithNumber(0).TestObject;
            Block changedHead = Build.A.Block.WithNumber(1).TestObject;

            headBlockChangeManager.ReturnSameGasPrice(Arg.Any<Block>(),
                    Arg.Is<Block>(a => a.Number == 0), Arg.Any<UInt256>())
                .Returns(false);
            headBlockChangeManager.ReturnSameGasPrice(Arg.Any<Block>(),
                    Arg.Is<Block>(a => a.Number == 1), Arg.Any<UInt256>())
                .Returns(true);
            
            ResultWrapper<UInt256?> firstResult = gasPriceOracle.GasPriceEstimate(initialHead, testDictionary);
            ResultWrapper<UInt256?> secondResult = gasPriceOracle.GasPriceEstimate(changedHead, testDictionary);
        }

        [Test]
        public void GasPriceEstimate_IfPreviousGasPriceExists_DefaultGasPriceIsSetToPreviousGasPrice()
        {
            
        }

        [Test]
        public void GasPriceEstimate_IfHeadBlockWasNotTheSame_AddValidTxAndReturnCountCalledNumberOfBlocksToGoBack()
        {
            
        }

        [Test]
        public void GasPriceEstimate_BlockcountEqualToBlocksToCheck_SixtiethPercentileOfMaxIndexReturned()
        {
            
        }

        [Test]
        public void GasPriceEstimate_IfCalculatedGasPriceGreaterThanMax_MaxGasPriceReturned()
        {
            
        }

        public GasPriceOracle GetGasPriceOracle(
            bool eip1559Enabled = false, 
            UInt256? ignoreUnder = null, 
            int? blockLimit = null, 
            UInt256? baseFee = null, 
            ITxInsertionManager txInsertionManager = null,
            IHeadBlockChangeManager headBlockChangeManager = null)
        {
            return new GasPriceOracle(
                eip1559Enabled,
                ignoreUnder,
                blockLimit,
                baseFee,
                txInsertionManager ?? Substitute.For<ITxInsertionManager>(),
                headBlockChangeManager ?? Substitute.For<IHeadBlockChangeManager>()
                );
        }
    }
}
