using Nethermind.Core.Specs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Core.Test.Specs
{
    [TestFixture]
    public class DynamicReleaseSpecTests
    {
        [Test]
        public void Test()
        {
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();
            specProvider.GetSpec(0).Returns(Olympic.Instance);
            specProvider.GetSpec(1).Returns(Frontier.Instance);
            DynamicReleaseSpec spec = new DynamicReleaseSpec(specProvider);
            Assert.False(spec.IsTimeAdjustmentPostOlympic);
            spec.CurrentBlockNumber = 1;
            Assert.True(spec.IsTimeAdjustmentPostOlympic);
        }
    }
}