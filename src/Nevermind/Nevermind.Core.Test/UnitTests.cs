using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nevermind.Core.Sugar;

namespace Nevermind.Core.Test
{
    [TestClass]
    public class UnitTests
    {
        [TestMethod]
        public void Ratios_are_correct()
        {
            Assert.AreEqual(Unit.Ether, Unit.Finney * 1000);
            Assert.AreEqual(Unit.Ether, Unit.Szabo * 1000 * 1000);
            Assert.AreEqual(Unit.Ether, Unit.Wei * 1000 * 1000 * 1000 * 1000 * 1000 * 1000);
        }
    }
}