//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Numerics;
using Nethermind.Crypto.ZkSnarks;
using NUnit.Framework;

namespace Nethermind.Core.Test.Crypto.ZkSnarks
{
    [TestFixture]
    public class FpTests
    {
        [Test]
        public void InverseOf2_initializes()
        {
            Fp _ = Fp.InverseOf2;
        }

        [Test]
        public void NonResidue_initializes()
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
            Assert.True(((Fp)2).Squared() == 4);
        }

        [Test]
        public void Negate_seems_fine()
        {
            Assert.True(-(Fp)1 == Parameters.P - 1);
        }
        
        [Test]
        public void Works_modulo()
        {
            Fp result = (Fp)1 - (Parameters.P - 1);
            Assert.AreEqual((Fp)2, result, "sub");
            
            result = (Fp)1 + (Parameters.P - 1);
            Assert.AreEqual((Fp)0, result, "add");
            
            result = ((Fp)Parameters.P - 1).Double();
            Assert.AreEqual((Fp)BigInteger.Parse("21888242871839275222246405745257275088696311157297823662689037894645226208581"), result, "double");
            
            result = ((Fp)Parameters.P - 1).Negate();
            Assert.AreEqual((Fp)1, result, "double");
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