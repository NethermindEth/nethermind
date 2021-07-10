using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules.Eth;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules
{
    [TestFixture]
    class EarlyExitManagerTests
    {
        [Test]
        public void ShouldReturnSameGasPrice_IfLastHeadAndCurrentHeadAreSame_WillReturnTrue()
        {
            Block testBlock = Build.A.Block.Genesis.TestObject;
            HeadBlockChangeManager headBlockChangeManager = new HeadBlockChangeManager();
            
            bool result = headBlockChangeManager.ShouldReturnSameGasPrice(testBlock, testBlock, 10);

            result.Should().BeTrue();
        }

        [Test]
        public void ShouldReturnSameGasPrice_IfLastHeadAndCurrentHeadAreNotSame_WillReturnFalse()
        {
            Block testBlock = Build.A.Block.Genesis.TestObject;
            Block differentTestBlock = Build.A.Block.WithNumber(1).TestObject;
            HeadBlockChangeManager headBlockChangeManager = new HeadBlockChangeManager();
            
            bool result = headBlockChangeManager.ShouldReturnSameGasPrice(testBlock, differentTestBlock, 10);

            result.Should().BeFalse();
        }

        [Test]
        public void ShouldReturnSameGasPrice_IfLastHeadIsNull_WillReturnFalse()
        {
            Block testBlock = Build.A.Block.Genesis.TestObject;
            HeadBlockChangeManager headBlockChangeManager = new HeadBlockChangeManager();
            
            bool result = headBlockChangeManager.ShouldReturnSameGasPrice(null, testBlock, 10);

            result.Should().BeFalse();
        }
        
        [Test]
        public void ShouldReturnSameGasPrice_IfLastGasPriceIsNull_WillReturnFalse()
        {
            Block testBlock = Build.A.Block.Genesis.TestObject;
            HeadBlockChangeManager headBlockChangeManager = new HeadBlockChangeManager();
            
            bool result = headBlockChangeManager.ShouldReturnSameGasPrice(testBlock, testBlock, null);

            result.Should().BeFalse();
        }
    }
}
