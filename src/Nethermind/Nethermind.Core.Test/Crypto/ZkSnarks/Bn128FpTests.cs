using Nethermind.Core.Crypto.ZkSnarks;
using NUnit.Framework;

namespace Nethermind.Core.Test.Crypto.ZkSnarks
{
    [TestFixture]
    public class Bn128FpTests
    {
        [Test]
        public void Equals_works_with_nulls()
        {
            Bn128Fp bn128Fp = new Bn128Fp(1, 1, 1);
            Assert.False(bn128Fp == null, "null to the right");
            Assert.False(null == bn128Fp, "null to the left");
            Assert.True((Bn128Fp)null == null, "null both sides");
        }
    }
}