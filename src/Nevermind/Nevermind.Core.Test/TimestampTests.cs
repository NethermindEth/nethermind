using System.Numerics;
using NUnit.Framework;

namespace Nevermind.Core.Test
{
    public class TimestampTests
    {
        [Test]
        public void Test()
        {
            BigInteger stamp = Timestamp.UtcNow;
            Assert.Greater(0, BigInteger.Compare(1507626119, stamp));
        }
    }
}