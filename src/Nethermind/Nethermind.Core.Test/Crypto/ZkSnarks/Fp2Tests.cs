/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

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
        public void FrobeniusCoefficientsB_initializes()
        {
            Fp[] _ = Fp2.FrobeniusCoefficientsB;
            Assert.True(_[0].Equals(Fp.One));
            Assert.True(!_[1].Equals(Fp.One));
        }

        [Test]
        public void Zero_initializes()
        {
            Fp2 _ = Fp2.Zero;
            Assert.True(_.A.Equals(Fp.Zero));
            Assert.True(_.B.Equals(Fp.Zero));
        }

        [Test]
        public void One_initializes()
        {
            Fp2 _ = Fp2.One;
            Assert.True(_.A.Equals(Fp.One));
            Assert.True(_.B.Equals(Fp.Zero));
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
            Fp2 result = Fp2.One.Squared();
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
            Assert.False(Fp2.Zero.Equals(null));
        }

        [Test]
        public void All_constructors_are_fine()
        {
            Fp2 a = new Fp2(1, 0);
            Fp2 b = new Fp2(new byte[] {1}, new byte[] {0});

            Assert.AreEqual(a, Fp2.One);
            Assert.AreEqual(b, Fp2.One);
        }

        [Test]
        public void Square_cross_check()
        {
            Fp2 a = new Fp2(Parameters.P / 2, Parameters.P / 4);
            a.IsValid();

            Assert.AreEqual(a.Squared(), a.Mul(a));
        }

        [Test]
        public void Double_cross_check()
        {
            Fp2 a = new Fp2(Parameters.P / 2, Parameters.P / 4);
            a.IsValid();

            Assert.AreEqual(a.Double(), a.Add(a));
        }

        [Test]
        public void Mul_schoolbook_check()
        {
            Fp2 a = new Fp2(Parameters.P / 2, Parameters.P / 4);
            Fp2 b = new Fp2(Parameters.P / 2, Parameters.P / 4);

            Assert.AreEqual(a.Mul(b), a.MulSchoolbook(b));
        }

        [Test]
        public void Mul_karatsuba_check()
        {
            Fp2 a = new Fp2(Parameters.P / 2, Parameters.P / 4);
            Fp2 b = new Fp2(Parameters.P / 2, Parameters.P / 4);

            Assert.AreEqual(a.Mul(b), a.MulKaratsuba(b));
        }

        [Test]
        public void Squared_schoolbook_check()
        {
            Fp2 a = new Fp2(Parameters.P / 2, Parameters.P / 4);
            Assert.AreEqual(a.Squared(), a.SquaredSchoolbook());
        }

        [Test]
        public void Squared_karatsuba_check()
        {
            Fp2 a = new Fp2(Parameters.P / 2, Parameters.P / 4);
            Assert.AreEqual(a.Squared(), a.SquaredKaratsuba());
        }

        [Test]
        public void Squared_complex_check()
        {
            Fp2 a = new Fp2(Parameters.P / 2, Parameters.P / 4);
            Assert.AreEqual(a.Squared(), a.SquaredComplex());
        }

        [Test]
        public void Add_negate()
        {
            Fp2 a = new Fp2(Parameters.P / 2, Parameters.P / 4);
            Assert.True(a.IsValid());
            
            Assert.AreEqual(Fp2.Zero, a.Add(a.Negate()));
            Assert.AreEqual(Fp2.Zero, a.Negate().Add(a));
        }

        [Test]
        public void Inverse_mul_self()
        {
            Fp2 a = new Fp2(Parameters.P / 2, Parameters.P / 4);
            Assert.AreEqual(Fp2.One, a.Mul(a.Inverse()));
            Assert.AreEqual(Fp2.One, a.Inverse().Mul(a));
        }
        
        [Test]
        public void Inverse_inverse()
        {
            Fp2 a = new Fp2(Parameters.P / 2, Parameters.P / 4);
            Assert.AreEqual(a, a.Inverse().Inverse());
        }

        [Test]
        public void Negate_negate()
        {
            Fp2 a = new Fp2(Parameters.P / 2, Parameters.P / 4);
            Assert.AreEqual(a, a.Negate().Negate());
        }
    }
}