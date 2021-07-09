using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules
{
    [TestFixture]
    class TxInsertionManagerTests
    {
        [Test]
        public void AddValidTxAndReturnCount_IfThresholdExists_IgnorePricesUnderIt()
        {
            
        }

        [Test]
        public void AddValidTxAndReturnCount_IfBlockHasMoreThanThreeTx_AddOnlyThree()
        {
            
        }

        [Test]
        public void AddValidTxAndReturnCount_IfBlockHasTxs_OnlyAddTxsWithLowestEffectiveGasPrice()
        {
            
        }
        
        [Test]
        public void AddValidTxAndReturnCount_TxsWithPriceLessThanIgnoreUnder_ShouldNotHaveGasPriceInTxGasPriceList()
        {
            
        }
        
        [Test]
        public void AddValidTxAndReturnCount_TxsSentByMiner_ShouldNotHaveGasPriceInTxGasPriceList()
        {
            
        }

        [Test]
        public void AddValidTxAndReturnCount_WhenEip1559Enabled_EffectiveGasPriceOfTxsCalculated()
        {
            
        }
    }
}
