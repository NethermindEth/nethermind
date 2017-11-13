using System.Collections;
using System.Linq;
using Nevermind.Core.Encoding;
using Nevermind.Core.Sugar;
using NUnit.Framework;

namespace Nevermind.Core.Test
{
    [TestFixture]
    public class BloomTests
    {
        [Test]
        public void Test()
        {
            Bloom bloom = new Bloom();
            bloom.Set(Keccak.OfAnEmptyString.Bytes);
            byte[] bytes = bloom.Bytes;
            BitArray bits = bytes.ToBigEndianBitArray2048();
            Bloom bloom2 = new Bloom(bits);
            Assert.AreEqual(bloom.ToString(), bloom2.ToString());
        }
    }
}