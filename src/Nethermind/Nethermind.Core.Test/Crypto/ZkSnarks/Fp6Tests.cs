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
    public class Fp6Tests
    {
        [Test]
        public void Equals_works_with_nulls()
        {
            Assert.False(Fp6.One.Equals(null), "null to the right");
        }
        
        [Test]
        public void Zero_initializes()
        {
            Fp6 _ = Fp6.Zero;
            Assert.AreEqual(Fp2.Zero, _.A, "A");
            Assert.AreEqual(Fp2.Zero, _.B, "B");
            Assert.AreEqual(Fp2.Zero, _.C, "C");
        }

        [Test]
        public void One_initializes()
        {
            Fp6 _ = Fp6.One;
            Assert.AreEqual(Fp2.One, _.A, "A");
            Assert.AreEqual(Fp2.Zero, _.B, "B");
            Assert.AreEqual(Fp2.Zero, _.C, "C");
        }
        
        [Test]
        public void One_is_reused()
        {
            // ReSharper disable once EqualExpressionComparison
            Assert.True(ReferenceEquals(Fp6.One, Fp6.One));
        }

        [Test]
        public void Zero_is_reused()
        {
            // ReSharper disable once EqualExpressionComparison
            Assert.True(ReferenceEquals(Fp6.Zero, Fp6.Zero));
        }

        [Test]
        public void NonResidue_is_reused()
        {
            // ReSharper disable once EqualExpressionComparison
            Assert.True(ReferenceEquals(Fp6.NonResidue, Fp6.NonResidue));
        }
        
        [Test]
        public void Square_cross_check()
        {
            Fp2 a2 = new Fp2(Parameters.P / 2, Parameters.P / 4);
            Fp6 a = new Fp6(a2, a2, a2);
            Assert.True(a.IsValid());
            
            Assert.AreEqual(a.Squared(), a.Mul(a));
        }
        
        [Test]
        public void Double_cross_check()
        {
            Fp2 a2 = new Fp2(Parameters.P / 2, Parameters.P / 4);
            Fp6 a = new Fp6(a2, a2, a2);
            Assert.True(a.IsValid());
            
            Assert.AreEqual(a.Double(), a.Add(a));
        }
        
        [Test]
        public void Add_negate()
        {
            Fp2 a2 = new Fp2(Parameters.P / 2, Parameters.P / 4);
            Fp6 a = new Fp6(a2, a2, a2);
            Assert.True(a.IsValid());
            
            Assert.AreEqual(Fp6.Zero, a.Add(a.Negate()));
            Assert.AreEqual(Fp6.Zero, a.Negate().Add(a));
        }
        
        [Test]
        public void Inverse_inverse()
        {
            Fp2 a2 = new Fp2(Parameters.P / 2, Parameters.P / 4);
            Fp6 a = new Fp6(a2, a2, a2);
            Assert.True(a.IsValid());
            
            Assert.AreEqual(a, a.Inverse().Inverse());
        }
        
        [Test]
        public void Inverse_mul_self()
        {
            Fp2 a2 = new Fp2(Parameters.P / 2, Parameters.P / 4);
            Fp6 a = new Fp6(a2, a2, a2);
            Assert.True(a.IsValid());
            
            Assert.AreEqual(Fp6.One, a.Mul(a.Inverse()));
        }
        
        [Test]
        public void Inverse_mul_self_is_commutative_regression()
        {
            Fp2 a2 = new Fp2(Parameters.P / 2, Parameters.P / 4);
            Fp6 a = new Fp6(a2, a2, a2);
            Assert.True(a.IsValid());

            Fp6 inv = a.Inverse();
            Assert.AreEqual(a.Mul(inv), inv.Mul(a));
        }
        
        [Test]
        public void Negate_negate()
        {
            Fp2 a2 = new Fp2(Parameters.P / 2, Parameters.P / 4);
            Fp6 a = new Fp6(a2, a2, a2);
            Assert.True(a.IsValid());
            
            Assert.AreEqual(a, a.Negate().Negate());
        }
    }
}