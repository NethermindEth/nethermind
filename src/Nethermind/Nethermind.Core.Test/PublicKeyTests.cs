// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class PublicKeyTests
    {
        [Test]
        public void Bytes_in_are_bytes_stored()
        {
            byte[] bytes = new byte[64];
            PublicKey publicKey = new(bytes);
            Assert.AreEqual(bytes, publicKey.Bytes);
        }

        [Test]
        public void Address_is_correct()
        {
            byte[] bytes = new byte[64];
            PublicKey publicKey = new(bytes);
            Address address = publicKey.Address;
            string addressString = address.ToString();
            Assert.AreEqual("0x3f17f1962b36e491b30a40b2405849e597ba5fb5", addressString);
        }

        [Test]
        public void Same_address_is_returned_when_called_twice()
        {
            byte[] bytes = new byte[64];
            PublicKey publicKey = new(bytes);
            Address address1 = publicKey.Address;
            Address address2 = publicKey.Address;
            Assert.AreSame(address1, address2);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(32)]
        [TestCase(63)]
        public void Cannot_be_initialized_with_array_of_length_different_than_64(int length)
        {
            byte[] bytes = new byte[length];
            // ReSharper disable once ObjectCreationAsStatement
            Assert.Throws<ArgumentException>(() => new PublicKey(bytes));
        }

        [Test]
        public void Initialization_with_65_bytes_should_be_prefixed_with_0x04()
        {
            byte[] bytes = new byte[65];
            // ReSharper disable once ObjectCreationAsStatement
            Assert.Throws<ArgumentException>(() => new PublicKey(bytes));
        }

        [Test]
        public void Can_initialize_with_correct_65_bytes()
        {
            byte[] bytes = new byte[65];
            bytes[0] = 0x04;
            // ReSharper disable once ObjectCreationAsStatement
            new PublicKey(bytes);
        }

        [Test]
        public void Cannot_be_initialized_with_null()
        {
            // ReSharper disable once ObjectCreationAsStatement
            Assert.Throws<ArgumentNullException>(() => new PublicKey((string)null!));
            Assert.Throws<ArgumentException>(() => new PublicKey((byte[])null!));
        }

        [Test]
        public void Can_be_initialized_with_an_empty_array_of_64_bytes()
        {
            byte[] bytes = new byte[64];
            // ReSharper disable once ObjectCreationAsStatement
            new PublicKey(bytes);
        }

        [Test]
        [Explicit]
        public void Generate_Keys()
        {
            var key = new PrivateKey(new CryptoRandom().GenerateRandomBytes(32));
            TestContext.Out.WriteLine(key);
            TestContext.Out.WriteLine(key.PublicKey);
            TestContext.Out.WriteLine(key.Address);
        }
    }
}
