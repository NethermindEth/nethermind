//  Copyright (c) 2021 Demerzel Solutions Limited
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

using System;
using System.IO;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class PrivateKeyTests
    {
        private const string TestPrivateKeyHex = "0x3a1076bf45ab87712ad64ccb3b10217737f7faacbf2872e88fdd9a537d8fe266";

        [OneTimeSetUp]
        public void SetUp()
        {
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(16)]
        [TestCase(31)]
        [TestCase(33)]
        public void Cannot_be_initialized_with_array_of_length_different_than_32(int length)
        {
            byte[] bytes = new byte[length];
            // ReSharper disable once ObjectCreationAsStatement
            Assert.Throws<ArgumentException>(() => new PrivateKey(bytes));
        }

        [Test]
        public void Cannot_be_initialized_with_null_bytes()
        {
            // ReSharper disable once ObjectCreationAsStatement
            Assert.Throws<ArgumentNullException>(() => new PrivateKey((byte[]) null));
        }

        [Test]
        public void Cannot_be_initialized_with_null_string()
        {
            // ReSharper disable once ObjectCreationAsStatement
            Assert.Throws<ArgumentNullException>(() => new PrivateKey((string) null));
        }

        [Test]
        public void Bytes_are_stored_correctly()
        {
            byte[] bytes = new byte[32];
            new Random(12).NextBytes(bytes);
            PrivateKey privateKey = new PrivateKey(bytes);
            Assert.True(Bytes.AreEqual(bytes, privateKey.KeyBytes));
        }

        [TestCase(TestPrivateKeyHex)]
        public void String_representation_is_correct(string hexString)
        {
            PrivateKey privateKey = new PrivateKey(hexString);
            string privateKeyString = privateKey.ToString();
            Assert.AreEqual(hexString, privateKeyString);
        }

        [TestCase("3a1076bf45ab87712ad64ccb3b10217737f7faacbf2872e88fdd9a537d8fe266", "0xc2d7cf95645d33006175b78989035c7c9061d3f9")]
        [TestCase("56e044e40c2d225593bc0a4ae3fd4a31ab11f9351f98e60109c1fb429b52e876", "0xd1dc4a77be62d06f0760187be2e505d270c170fd")]
        public void Address_as_expected(string privateKeyHex, string addressHex)
        {
            PrivateKey privateKey = new PrivateKey(privateKeyHex);
            Address address = privateKey.Address;
            Assert.AreEqual(addressHex, address.ToString());
        }

        [Test]
        public void Address_returns_the_same_value_when_called_twice()
        {
            PrivateKey privateKey = new PrivateKey(TestPrivateKeyHex);
            Address address1 = privateKey.Address;
            Address address2 = privateKey.Address;
            Assert.AreSame(address1, address2);
        }

        /// <summary>
        /// https://en.bitcoin.it/wiki/Private_key
        /// </summary>
        [TestCase("0000000000000000000000000000000000000000000000000000000000000000", false)]
        [TestCase("0000000000000000000000000000000000000000000000000000000000000001", true)]
        [TestCase("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364140", true)]
        [TestCase("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141", false)]
        public void Fails_on_invalid(string hex, bool expectedValid)
        {
            if (!expectedValid)
            {
                Assert.Throws<ArgumentException>(() => _ = new PrivateKey(Bytes.FromHexString(hex)));
            }
            else
            {
                _ = new PrivateKey(Bytes.FromHexString(hex));
            }
        }
    }
}
