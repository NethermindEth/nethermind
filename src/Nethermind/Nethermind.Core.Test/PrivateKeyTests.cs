// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Core.Crypto;
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
            Assert.Throws<ArgumentNullException>(() => new PrivateKey((byte[])null!));
        }

        [Test]
        public void Cannot_be_initialized_with_null_string()
        {
            // ReSharper disable once ObjectCreationAsStatement
            Assert.Throws<ArgumentNullException>(() => new PrivateKey((string)null!));
        }

        [Test]
        public void Bytes_are_stored_correctly()
        {
            byte[] bytes = new byte[32];
            new Random(12).NextBytes(bytes);
            PrivateKey privateKey = new(bytes);
            Assert.True(Bytes.AreEqual(bytes, privateKey.KeyBytes));
        }

        [TestCase(TestPrivateKeyHex)]
        public void String_representation_is_correct(string hexString)
        {
            PrivateKey privateKey = new(hexString);
            string privateKeyString = privateKey.ToString();
            Assert.That(privateKeyString, Is.EqualTo(hexString));
        }

        [TestCase("3a1076bf45ab87712ad64ccb3b10217737f7faacbf2872e88fdd9a537d8fe266", "0xc2d7cf95645d33006175b78989035c7c9061d3f9")]
        [TestCase("56e044e40c2d225593bc0a4ae3fd4a31ab11f9351f98e60109c1fb429b52e876", "0xd1dc4a77be62d06f0760187be2e505d270c170fd")]
        public void Address_as_expected(string privateKeyHex, string addressHex)
        {
            PrivateKey privateKey = new(privateKeyHex);
            Address address = privateKey.Address;
            Assert.That(address.ToString(), Is.EqualTo(addressHex));
        }

        [Test]
        public void Address_returns_the_same_value_when_called_twice()
        {
            PrivateKey privateKey = new(TestPrivateKeyHex);
            Address address1 = privateKey.Address;
            Address address2 = privateKey.Address;
            Assert.That(address2, Is.SameAs(address1));
        }

        [Test]
        public void Can_decompress_public_key()
        {
            PrivateKey privateKey = new(TestPrivateKeyHex);
            PublicKey a = privateKey.PublicKey;
            PublicKey b = privateKey.CompressedPublicKey.Decompress();
            Assert.That(b, Is.EqualTo(a));
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
