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
    public class Fp12Tests
    {
        [Test]
        public void Equals_works_with_nulls()
        {
            Assert.False(Fp12.One == null, "null to the right");
            Assert.False(null == Fp12.One, "null to the left");
            Assert.True((Fp12)null == null, "null both sides");
        }
        
        [Test]
        public void Zero_initializes()
        {
            Fp12 _ = Fp12.Zero;
            Assert.AreEqual(Fp6.Zero, _.A, "A");
            Assert.AreEqual(Fp6.Zero, _.B, "B");
        }

        [Test]
        public void One_initializes()
        {
            Fp12 _ = Fp12.One;
            Assert.AreEqual(Fp6.One, _.A, "A");
            Assert.AreEqual(Fp6.Zero, _.B, "B");
        }
        
        [Test]
        public void One_is_reused()
        {
            // ReSharper disable once EqualExpressionComparison
            Assert.True(ReferenceEquals(Fp12.One, Fp12.One));
        }

        [Test]
        public void Zero_is_reused()
        {
            // ReSharper disable once EqualExpressionComparison
            Assert.True(ReferenceEquals(Fp12.Zero, Fp12.Zero));
        }
        
        [Test]
        public void Unitary_inverse_seems_fine()
        {
            // ReSharper disable once EqualExpressionComparison
            Fp6 oneOneOne = new Fp6(Fp2.One, Fp2.One, Fp2.One);
            Fp12 unitaryInverted = new Fp12(oneOneOne, oneOneOne).UnitaryInverse();
            Assert.AreEqual(oneOneOne, unitaryInverted.A, "A");
            Assert.AreEqual(oneOneOne.Negate(), unitaryInverted.B, "B");
        }
        
        [Test]
        public void Cyclotomic_exponentiation_seems_fine()
        {
            // ReSharper disable once EqualExpressionComparison
            Fp12 cyclotomicSquare = Fp12.One.CyclotomicExp(3);
            Assert.AreEqual(Fp6.One, cyclotomicSquare.A, "A");
            Assert.AreEqual(Fp6.Zero, cyclotomicSquare.B, "B");
        }
        
        [Test]
        public void Cyclotomic_square_seems_fine()
        {
            // ReSharper disable once EqualExpressionComparison
            Fp12 cyclotomicSquare = Fp12.One.CyclotomicSquare();
            Assert.AreEqual(Fp6.One, cyclotomicSquare.A, "A");
            Assert.AreEqual(Fp6.Zero, cyclotomicSquare.B, "B");
        }
        
        [Test]
        public void Square_cross_check()
        {
            Fp2 a2 = new Fp2(Parameters.P / 2, Parameters.P / 4);
            Fp6 a6 = new Fp6(a2, a2, a2);
            Fp12 a12 = new Fp12(a6, a6);
            Assert.True(a12.IsValid());
            
            Assert.AreEqual(a12.Squared(), a12.Mul(a12));
        }
    }
}