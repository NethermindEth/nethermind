using System.Numerics;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class BigIntegerTests
    {
        [TestCase(0, 1)]
        [TestCase(1, 1)]
        [TestCase(2, 2)]
        [TestCase(3, 2)]
        [TestCase(4, 3)]
        [TestCase(1024, 11)]
        public void Bit_length_is_calculated_properly(int value, int expectedBitLength)
        {
            Assert.AreEqual(expectedBitLength, new BigInteger(value).BitLength());
        }

        [TestCase(0, 0, false)]
        [TestCase(1, 0, true)]
        [TestCase(2, 0, false)]
        [TestCase(2, 1, true)]
        [TestCase(2, 2, false)]
        [TestCase(1024, 10, true)]
        public void Test_bit_is_calculated_properly(int value, int bitIndex, bool expectedResult)
        {
            Assert.AreEqual(expectedResult, new BigInteger(value).TestBit(bitIndex));
        }
        
        [Test]
        public void Test_bit_regression()
        {
            Assert.AreEqual(true, BigInteger.Pow(2, 128).TestBit(128), "128");
            Assert.AreEqual(false, BigInteger.Pow(2, 128).TestBit(95), "95");
            Assert.AreEqual(false, BigInteger.Pow(2, 128).TestBit(0), "0");
        }
    }
}