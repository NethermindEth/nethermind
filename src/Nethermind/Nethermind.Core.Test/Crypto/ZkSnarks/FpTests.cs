using System.Numerics;
using Nethermind.Core.Crypto.ZkSnarks;
using NUnit.Framework;

namespace Nethermind.Core.Test.Crypto.ZkSnarks
{
    [TestFixture]
    public class FpTests
    {
        [Test]
        public void InverseOf2_intiializes()
        {
            Fp _ = Fp.InverseOf2;
        }

        [Test]
        public void NonResidue_intiializes()
        {
            Fp _ = Fp.NonResidue;
        }

        [Test]
        public void Zero_initializes()
        {
            Fp _ = Fp.Zero;
        }

        [Test]
        public void One_initializes()
        {
            Fp _ = Fp.One;
        }

        [Test]
        public void One_is_reused()
        {
            // ReSharper disable once EqualExpressionComparison
            Assert.True(ReferenceEquals(Fp.One, Fp.One));
        }

        [Test]
        public void Zero_is_reused()
        {
            // ReSharper disable once EqualExpressionComparison
            Assert.True(ReferenceEquals(Fp.Zero, Fp.Zero));
        }

        [Test]
        public void InverseOf2_is_reused()
        {
            // ReSharper disable once EqualExpressionComparison
            Assert.True(ReferenceEquals(Fp.InverseOf2, Fp.InverseOf2));
        }

        [Test]
        public void NonResidue_is_reused()
        {
            // ReSharper disable once EqualExpressionComparison
            Assert.True(ReferenceEquals(Fp.NonResidue, Fp.NonResidue));
        }

        [Test]
        public void Equals_is_fine_for_one()
        {
            Assert.True(Fp.One.Equals(Fp.One));
        }

        [Test]
        public void Equality_operators_seem_fine()
        {
            Assert.False(Fp.One == Fp.Zero);
            Assert.True(Fp.One != Fp.Zero);
        }

        [Test]
        public void Implicit_operators_seem_fine()
        {
            Fp a = 1;
            Fp b = 1U;
            Fp c = 1L;
            Fp d = 1UL;
            Fp e = new BigInteger(1);

            Assert.True(a == b && a == c && a == d && a == e);
        }

        [Test]
        public void Implicit_operators_reuse_zero()
        {
            Fp a = 0;
            Fp b = 0U;
            Fp c = 0L;
            Fp d = 0UL;
            Fp e = new BigInteger(0);

            Assert.True(ReferenceEquals(a, Fp.Zero), a.GetType().Name);
            Assert.True(ReferenceEquals(b, Fp.Zero), a.GetType().Name);
            Assert.True(ReferenceEquals(c, Fp.Zero), a.GetType().Name);
            Assert.True(ReferenceEquals(d, Fp.Zero), a.GetType().Name);
            Assert.True(ReferenceEquals(e, Fp.Zero), a.GetType().Name);
        }

        [Test]
        public void Implicit_operators_reuse_one()
        {
            Fp a = 1;
            Fp b = 1U;
            Fp c = 1L;
            Fp d = 1UL;
            Fp e = new BigInteger(1);

            Assert.True(ReferenceEquals(a, Fp.One), a.GetType().Name);
            Assert.True(ReferenceEquals(b, Fp.One), a.GetType().Name);
            Assert.True(ReferenceEquals(c, Fp.One), a.GetType().Name);
            Assert.True(ReferenceEquals(d, Fp.One), a.GetType().Name);
            Assert.True(ReferenceEquals(e, Fp.One), a.GetType().Name);
        }

        [Test]
        public void Add_seems_fine()
        {
            Assert.True((Fp)1 + 1 == 2);
        }

        [Test]
        public void Sub_seems_fine()
        {
            Assert.True((Fp)1 - 1 == 0);
        }

        [Test]
        public void Mul_seems_fine()
        {
            Assert.True((Fp)1 * 1 == 1);
        }

        [Test]
        public void Double_seems_fine()
        {
            Assert.True(((Fp)1).Double() == 2);
        }

        [Test]
        public void Square_seems_fine()
        {
            Assert.True(((Fp)2).Square() == 4);
        }

        [Test]
        public void Negate_seems_fine()
        {
            Assert.True(((Fp)1).Negate() == -1);
        }

        [Test]
        public void IsValid_seems_fine()
        {
            Assert.True(((Fp)1).IsValid());
        }

        [Test]
        public void IsZero_seems_fine()
        {
            Assert.True(((Fp)0).IsZero(), "true case");
            Assert.False(((Fp)1).IsZero(), "false case");
        }
        
        [Test]
        public void Equals_handles_null()
        {
            Assert.False((Fp)0 == null, "null to the right");
            Assert.False(null == (Fp)0, "null to the left");
            Assert.True((Fp)null == null, "null both sides");
        }

        [Test]
        public void All_constructors_are_fine()
        {
            Fp a = new Fp(new BigInteger(2));
            Fp b = new Fp(new byte[] {2});
            
            Assert.AreEqual(a, (Fp)2);
            Assert.AreEqual(b, (Fp)2);
        }
    }
}