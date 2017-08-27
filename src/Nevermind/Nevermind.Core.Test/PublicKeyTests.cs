using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Nevermind.Core.Test
{
    [TestClass]
    public class PublicKeyTests
    {
        [TestMethod]
        public void Address_is_correct()
        {
            byte[] bytes = new byte[64];
            PublicKey publicKey = new PublicKey(bytes);
            Address address = publicKey.Address;
            string addressString = address.ToString();
            Assert.AreEqual("0x3f17f1962b36e491b30a40b2405849e597ba5fb5", addressString);
        }

        [TestMethod]
        public void Same_address_is_returned_when_called_twice()
        {
            byte[] bytes = new byte[64];
            PublicKey publicKey =  new PublicKey(bytes);
            Address address1 = publicKey.Address;
            Address address2 = publicKey.Address;
            Assert.AreSame(address1, address2);
        }

        [DataTestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(32)]
        [DataRow(63)]
        [ExpectedException(typeof(ArgumentException))]
        public void Cannot_be_initialized_with_array_of_length_different_than_64(int length)
        {
            byte[] bytes = new byte[length];
            // ReSharper disable once ObjectCreationAsStatement
            new PublicKey(bytes);
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void Initialization_with_65_bytes_should_be_prefixed_with_0x04()
        {
            byte[] bytes = new byte[65];
            // ReSharper disable once ObjectCreationAsStatement
            new PublicKey(bytes);
        }

        [TestMethod]
        public void Can_initialize_with_correct_65_bytes()
        {
            byte[] bytes = new byte[65];
            bytes[0] = 0x04;
            // ReSharper disable once ObjectCreationAsStatement
            new PublicKey(bytes);
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentNullException))]
        public void Cannot_be_initialized_with_null()
        {
            // ReSharper disable once ObjectCreationAsStatement
            new PublicKey(null);
        }

        [TestMethod]
        public void Can_be_initialized_with_an_empty_array_of_64_bytes()
        {
            byte[] bytes = new byte[64];
            // ReSharper disable once ObjectCreationAsStatement
            new PublicKey(bytes);
        }
    }
}
