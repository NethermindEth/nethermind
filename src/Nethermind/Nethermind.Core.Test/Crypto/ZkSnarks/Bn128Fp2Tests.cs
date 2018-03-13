using Nethermind.Core.Crypto.ZkSnarks;
using NUnit.Framework;

namespace Nethermind.Core.Test.Crypto.ZkSnarks
{
    [TestFixture]
    public class Bn128Fp2Tests
    {
        [Test]
        public void Equals_works_with_nulls()
        {
            Bn128Fp2 bn128Fp2 = new Bn128Fp2(Fp2.One, Fp2.One, Fp2.One);
            Assert.False(bn128Fp2 == null, "null to the right");
            Assert.False(null == bn128Fp2, "null to the left");
            Assert.True((Bn128Fp2)null == null, "null both sides");
        }

        [Test]
        public void Zero_initializes()
        {   
            Bn128Fp2 p1 = Bn128Fp2.Create(new byte[] {0}, new byte[] {0}, new byte[] {0}, new byte[] {0});
            Assert.NotNull(p1.Zero);
        }
        
        [Test]
        public void Zero_reused()
        {   
            Bn128Fp2 p1 = Bn128Fp2.Create(new byte[] {0}, new byte[] {0}, new byte[] {0}, new byte[] {0});
            // ReSharper disable once EqualExpressionComparison
            Assert.True(ReferenceEquals(p1.Zero, p1.Zero));
        }
    }
}