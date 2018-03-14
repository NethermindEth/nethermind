using Nethermind.Core.Crypto.ZkSnarks;
using NUnit.Framework;

namespace Nethermind.Core.Test.Crypto.ZkSnarks
{
    [TestFixture]
    public class Fp2Tests
    {
        [Test]
        public void NonResidue_intiializes()
        {
            Fp2 _ = Fp2.NonResidue;
        }

        [Test]
        public void FrobeniusCoefficientsB_initialize()
        {
            Fp[] _ = Fp2.FrobeniusCoefficientsB;
            Assert.True(_[0] == Fp.One);
            Assert.True(_[1] != Fp.One);
        }
        
        [Test]
        public void Zero_initializes()
        {
            Fp2 _ = Fp2.Zero;
            Assert.True(_.A == Fp.Zero);
            Assert.True(_.B == Fp.Zero);
        }

        [Test]
        public void One_initializes()
        {
            Fp2 _ = Fp2.One;
            Assert.True(_.A == Fp.One);
            Assert.True(_.B == Fp.Zero);
        }

        [Test]
        public void One_is_reused()
        {
            // ReSharper disable once EqualExpressionComparison
            Assert.True(ReferenceEquals(Fp2.One, Fp2.One));
        }

        [Test]
        public void Zero_is_reused()
        {
            // ReSharper disable once EqualExpressionComparison
            Assert.True(ReferenceEquals(Fp2.Zero, Fp2.Zero));
        }

        [Test]
        public void NonResidue_is_reused()
        {
            // ReSharper disable once EqualExpressionComparison
            Assert.True(ReferenceEquals(Fp2.NonResidue, Fp2.NonResidue));
        }

        [Test]
        public void Equals_is_fine_for_one()
        {
            Assert.True(Fp2.One.Equals(Fp2.One));
        }

        [Test]
        public void Equality_operators_seem_fine()
        {
            Assert.False(Fp2.One == Fp2.Zero);
            Assert.True(Fp2.One != Fp2.Zero);
        }

        [Test]
        public void Add_seems_fine()
        {
            Fp2 result = Fp2.One.Add(Fp2.NonResidue);
            Assert.AreEqual((Fp)10, result.A);
            Assert.AreEqual((Fp)1, result.B);
        }

        [Test]
        public void Sub_seems_fine()
        {
            Fp2 result = Fp2.NonResidue.Sub(Fp2.NonResidue);
            Assert.AreEqual((Fp)0, result.A);
            Assert.AreEqual((Fp)0, result.B);
        }

        [Test]
        public void Mul_seems_fine()
        {
            Fp2 result = Fp2.One.Mul(Fp2.NonResidue);
            Assert.AreEqual((Fp)9, result.A);
            Assert.AreEqual((Fp)1, result.B);
        }
        
        [Test]
        public void MulByNonResidue_seems_fine()
        {
            Fp2 result = Fp2.One.MulByNonResidue();
            Assert.AreEqual((Fp)9, result.A);
            Assert.AreEqual((Fp)1, result.B);
        }
        
        [Test]
        public void Inverse_seems_fine()
        {
            Fp2 result = Fp2.One.Inverse();
            Assert.AreEqual((Fp)1, result.A);
            Assert.AreEqual((Fp)0, result.B);
        }
        
        [Test]
        public void FrobeniusMap_seems_fine()
        {
            Fp2 result = Fp2.NonResidue.FrobeniusMap(2);
            Assert.AreEqual((Fp)9, result.A);
            Assert.AreEqual((Fp)1, result.B);
        }

        [Test]
        public void Double_seems_fine()
        {
            Fp2 result = Fp2.NonResidue.Double();
            Assert.AreEqual((Fp)18, result.A);
            Assert.AreEqual((Fp)2, result.B);
        }

        [Test]
        public void Square_seems_fine()
        {
            Fp2 result = Fp2.One.Square();
            Assert.AreEqual((Fp)1, result.A);
            Assert.AreEqual((Fp)0, result.B);
        }

        [Test]
        public void Negate_seems_fine()
        {
            Fp2 result = Fp2.NonResidue.Negate();
            Assert.AreEqual((Fp)(Parameters.P - 9), result.A);
            Assert.AreEqual((Fp)(Parameters.P - 1), result.B);
        }

        [Test]
        public void IsValid_seems_fine()
        {
            Assert.True(Fp2.NonResidue.IsValid());
        }

        [Test]
        public void IsZero_seems_fine()
        {
            Assert.True(Fp2.Zero.IsZero(), "true case");
            Assert.False(Fp2.One.IsZero(), "false case");
        }
        
        [Test]
        public void Equals_handles_null()
        {
            Assert.False(Fp2.Zero == null, "null to the right");
            Assert.False(null == Fp2.Zero, "null to the left");
            Assert.True((Fp2)null == null, "null both sides");
        }

        [Test]
        public void All_constructors_are_fine()
        {
            Fp2 a = new Fp2(1, 0);
            Fp2 b = new Fp2(new byte[] {1}, new byte[] {0});
            
            Assert.AreEqual(a, Fp2.One);
            Assert.AreEqual(b, Fp2.One);
        }
    }
}