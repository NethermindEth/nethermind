using Nevermind.Core.Sugar;
using NUnit.Framework;

namespace Nevermind.Core.Test
{
    [TestFixture]
    public class UnitTests
    {
        [Test]
        public void Ratios_are_correct()
        {
            Assert.AreEqual(Unit.Ether, Unit.Finney * 1000);
            Assert.AreEqual(Unit.Ether, Unit.Szabo * 1000 * 1000);
            Assert.AreEqual(Unit.Ether, Unit.Wei * 1000 * 1000 * 1000 * 1000 * 1000 * 1000);
        }
    }
}