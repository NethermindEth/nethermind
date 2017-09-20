using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nevermind.Core.Signing;

namespace Nevermind.Core.Test
{
    [TestClass]
    public class PrivateKeyTests
    {
        private const string TestPrivateKeyHex = "0x3a1076bf45ab87712ad64ccb3b10217737f7faacbf2872e88fdd9a537d8fe266";

        [TestMethod]
        public void Can_generate_new_through_constructor()
        {
            PrivateKey privateKey = new PrivateKey();
            PrivateKey zeroKey = new PrivateKey(new byte[32]);
            Assert.AreNotEqual(privateKey.ToString(), zeroKey.ToString());
        }

        [DataTestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(16)]
        [DataRow(31)]
        [DataRow(33)]
        [ExpectedException(typeof(ArgumentException))]
        public void Cannot_be_initialized_with_array_of_length_different_than_32(int length)
        {
            byte[] bytes = new byte[length];
            // ReSharper disable once ObjectCreationAsStatement
            new PrivateKey(bytes);
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentNullException))]
        public void Cannot_be_initialized_with_null_bytes()
        {
            // ReSharper disable once ObjectCreationAsStatement
            new PrivateKey((byte[])null);
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentNullException))]
        public void Cannot_be_initialized_with_null_string()
        {
            // ReSharper disable once ObjectCreationAsStatement
            new PrivateKey((string)null);
        }

        [TestMethod]
        public void Bytes_are_retrievable()
        {
            byte[] bytes = new byte[32];
            new System.Random(12).NextBytes(bytes);
            PrivateKey privateKey = new PrivateKey(bytes);
            Assert.AreSame(bytes, privateKey.Bytes);
        }

        [DataTestMethod]
        [DataRow(TestPrivateKeyHex)]
        public void String_representation_is_correct(string hexString)
        {
            PrivateKey privateKey = new PrivateKey(hexString);
            string privateKeyString = privateKey.ToString();
            Assert.AreEqual(hexString, privateKeyString);
        }

        [DataTestMethod]
        [DataRow("3a1076bf45ab87712ad64ccb3b10217737f7faacbf2872e88fdd9a537d8fe266", "0xc2d7cf95645d33006175b78989035c7c9061d3f9")]
        [DataRow("56e044e40c2d225593bc0a4ae3fd4a31ab11f9351f98e60109c1fb429b52e876", "0xd1dc4a77be62d06f0760187be2e505d270c170fd")]
        public void Address_as_expected(string privateKeyHex, string addressHex)
        {
            PrivateKey privateKey = new PrivateKey(privateKeyHex);
            Address address = privateKey.Address;
            Assert.AreEqual(addressHex, address.ToString());
        }

        [TestMethod]
        public void Address_returns_the_same_value_when_called_twice()
        {
            PrivateKey privateKey = new PrivateKey(TestPrivateKeyHex);
            Address address1 = privateKey.Address;
            Address address2 = privateKey.Address;
            Assert.AreSame(address1, address2);
        }
    }
}
