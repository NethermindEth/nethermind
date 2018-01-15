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

using Nevermind.Core.Crypto;
using NUnit.Framework;

namespace Nevermind.Core.Test
{
    [TestFixture]
    public class AddressTests
    {
        [TestCase("0x5A4EAB120fB44eb6684E5e32785702FF45ea344D", "0x5a4eab120fb44eb6684e5e32785702ff45ea344d")]
        [TestCase("0x5a4eab120fb44eb6684e5e32785702ff45ea344d", "0x5a4eab120fb44eb6684e5e32785702ff45ea344d")]
        public void String_representation_is_correct(string init, string expected)
        {
            Address address = new Address(init);
            string addressString = address.ToString();
            Assert.AreEqual(expected, addressString);
        }

        [TestCase("0x52908400098527886E0F7030069857D2E4169EE7", "0x52908400098527886E0F7030069857D2E4169EE7")]
        [TestCase("0x8617E340B3D01FA5F11F306F4090FD50E238070D", "0x8617E340B3D01FA5F11F306F4090FD50E238070D")]
        [TestCase("0xde709f2102306220921060314715629080e2fb77", "0xde709f2102306220921060314715629080e2fb77")]
        [TestCase("0x27b1fdb04752bbc536007a920d24acb045561c26", "0x27b1fdb04752bbc536007a920d24acb045561c26")]
        [TestCase("0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed", "0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed")]
        [TestCase("0xfB6916095ca1df60bB79Ce92cE3Ea74c37c5d359", "0xfB6916095ca1df60bB79Ce92cE3Ea74c37c5d359")]
        [TestCase("0xdbF03B407c01E7cD3CBea99509d93f8DDDC8C6FB", "0xdbF03B407c01E7cD3CBea99509d93f8DDDC8C6FB")]
        [TestCase("0xD1220A0cf47c7B9Be7A2E6BA89F429762e7b9aDb", "0xD1220A0cf47c7B9Be7A2E6BA89F429762e7b9aDb")]
        [TestCase("0x5be4BDC48CeF65dbCbCaD5218B1A7D37F58A0741", "0x5be4BDC48CeF65dbCbCaD5218B1A7D37F58A0741")]
        [TestCase("0x5A4EAB120fB44eb6684E5e32785702FF45ea344D", "0x5A4EAB120fB44eb6684E5e32785702FF45ea344D")]
        [TestCase("0xa7dD84573f5ffF821baf2205745f768F8edCDD58", "0xa7dD84573f5ffF821baf2205745f768F8edCDD58")]
        [TestCase("0x027a49d11d118c0060746F1990273FcB8c2fC196", "0x027a49d11d118c0060746F1990273FcB8c2fC196")]
        public void String_representation_with_checksum_is_correct(string init, string expected)
        {
            Address address = new Address(init);
            string addressString = address.ToString(true);
            Assert.AreEqual(expected, addressString);
        }

        [Test] public void Hex_is_correctly_assigned()
        {
            byte[] bytes = new byte[20];
            new System.Random(1).NextBytes(bytes);
            Address address = new Address(bytes);
            Assert.AreEqual(address.Hex, new Hex(bytes));
        }

        [Test]
        public void Equals_works()
        {
            Address addressA = new Address(Keccak.Compute("a"));
            Address addressA2 = new Address(Keccak.Compute("a"));
            Address addressB = new Address(Keccak.Compute("b"));
            Assert.True(addressA.Equals(addressA2));
            // ReSharper disable once EqualExpressionComparison
            Assert.True(addressA.Equals(addressA));
            Assert.False(addressA.Equals(addressB));
            Assert.False(addressA.Equals(null));
        }
        
        [Test]
        public void Equals_operator_works()
        {
            Address addressA = new Address(Keccak.Compute("a"));
            Address addressA2 = new Address(Keccak.Compute("a"));
            Address addressB = new Address(Keccak.Compute("b"));
            Assert.True(addressA == addressA2);
            // ReSharper disable once EqualExpressionComparison
            Assert.True(addressA == addressA);
            Assert.False(addressA == addressB);
            Assert.False(addressA == null);
            Assert.False(null == addressA);
            Assert.True((Address)null == null);
        }
        
        [Test]
        public void Not_equals_operator_works()
        {
            Address addressA = new Address(Keccak.Compute("a"));
            Address addressA2 = new Address(Keccak.Compute("a"));
            Address addressB = new Address(Keccak.Compute("b"));
            Assert.False(addressA != addressA2);
            // ReSharper disable once EqualExpressionComparison
            Assert.False(addressA != addressA);
            Assert.True(addressA != addressB);
            Assert.True(addressA != null);
            Assert.True(null != addressA);
            Assert.False((Address)null != null);
        }
    }
}
