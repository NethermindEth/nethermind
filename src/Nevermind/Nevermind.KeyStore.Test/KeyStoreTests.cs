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

using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nevermind.Core;
using Nevermind.Json;
using Nevermind.Utils.Model;
using NUnit.Framework;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace Nevermind.KeyStore.Test
{
    [TestFixture]
    public class KeyStoreTests
    {
        private IKeyStore _store;
        private IJsonSerializer _serializer;
        private IConfigurationProvider _configurationProvider;
        private readonly string _testPass = "testpassword";

        [SetUp]
        public void Initialize()
        {
            _configurationProvider = new ConfigurationProvider();
            var logger = new ConsoleLogger();
            _serializer = new JsonSerializer(logger);
            _store = new FileKeyStore(_configurationProvider, _serializer, new AesEncrypter(_configurationProvider, logger), logger);
        }

        [Test]
        public void GenerateKeyTest()
        {
            //generate key
            var key = _store.GenerateKey(_testPass);
            Assert.AreEqual(key.Item2.ResultType, ResultType.Success);

            //get persisted key, verify it matches generated key
            var persistedKey = _store.GetKey(key.Item1.Address, _testPass);
            Assert.AreEqual(persistedKey.Item2.ResultType, ResultType.Success);
            Assert.IsTrue(key.Item1.Hex.Equals(persistedKey.Item1.Hex));

            //delete generated key
            var result = _store.DeleteKey(key.Item1.Address, _testPass);
            Assert.AreEqual(result.ResultType, ResultType.Success);

            //get created key, verify it does not exist anymore
            var deletedKey = _store.GetKey(key.Item1.Address, _testPass);
            Assert.AreEqual(deletedKey.Item2.ResultType, ResultType.Failure);
        }

        [Test]
        public void GenerateKeyAddressesTest()
        {
            //generate keys
            var key = _store.GenerateKey(_testPass);
            Assert.AreEqual(key.Item2.ResultType, ResultType.Success);

            var key2 = _store.GenerateKey(_testPass);
            Assert.AreEqual(key2.Item2.ResultType, ResultType.Success);

            //get key addreses
            var addresses = _store.GetKeyAddresses();
            Assert.AreEqual(addresses.Item2.ResultType, ResultType.Success);
            Assert.IsTrue(addresses.Item1.Count() >= 2);
            Assert.IsNotNull(addresses.Item1.FirstOrDefault(x => x.Equals(key.Item1.Address)));
            Assert.IsNotNull(addresses.Item1.FirstOrDefault(x => x.Equals(key2.Item1.Address)));

            //delete generated keys
            var result = _store.DeleteKey(key.Item1.Address, _testPass);
            Assert.AreEqual(result.ResultType, ResultType.Success);

            var result2 = _store.DeleteKey(key2.Item1.Address, _testPass);
            Assert.AreEqual(result2.ResultType, ResultType.Success);
        }

        [Test]
        public void WrongPasswordTest()
        {
            //generate key
            var key = _store.GenerateKey(_testPass);
            Assert.AreEqual(key.Item2.ResultType, ResultType.Success);

            //Get with right pass
            var key2 = _store.GetKey(key.Item1.Address, _testPass);
            Assert.AreEqual(key2.Item2.ResultType, ResultType.Success);
            Assert.AreEqual(key2.Item1.Hex, key.Item1.Hex);

            //Try to Get with wrong pass
            var key3 = _store.GetKey(key.Item1.Address, "wrongpass");
            Assert.AreEqual(key3.Item2.ResultType, ResultType.Failure);
            Assert.AreEqual(key3.Item2.Error, "Incorrect MAC");

            //delete generated key
            var result = _store.DeleteKey(key.Item1.Address, _testPass);
            Assert.AreEqual(result.ResultType, ResultType.Success);
        }

        [Test]
        public void KeyStoreVersionMismatchTest()
        {
            //generate key
            var key = _store.GenerateKey(_testPass);
            Assert.AreEqual(key.Item2.ResultType, ResultType.Success);

            //replace version
            var filePath = Path.Combine(_configurationProvider.KeyStoreDirectory, key.Item1.Address.ToString());
            var item = _serializer.Deserialize<KeyStoreItem>(File.ReadAllText(filePath));
            item.Version = 1;
            var json = _serializer.Serialize(item);
            File.WriteAllText(filePath, json);

            //try to read
            var key2 = _store.GetKey(key.Item1.Address, _testPass);
            Assert.AreEqual(key2.Item2.ResultType, ResultType.Failure);
            Assert.AreEqual(key2.Item2.Error, "KeyStore version mismatch");

            //clean up
            File.Delete(filePath);
        }

        [Test]
        public void CryptoVersionMismatchTest()
        {
            //generate key
            var key = _store.GenerateKey(_testPass);
            Assert.AreEqual(key.Item2.ResultType, ResultType.Success);

            //replace version
            var filePath = Path.Combine(_configurationProvider.KeyStoreDirectory, key.Item1.Address.ToString());
            var item = _serializer.Deserialize<KeyStoreItem>(File.ReadAllText(filePath));
            item.Crypto.Version = 0;
            var json = _serializer.Serialize(item);
            File.WriteAllText(filePath, json);

            //try to read
            var key2 = _store.GetKey(key.Item1.Address, _testPass);
            Assert.AreEqual(key2.Item2.ResultType, ResultType.Failure);
            Assert.AreEqual(key2.Item2.Error, "Crypto version mismatch");

            //clean up
            File.Delete(filePath);
        }
    }
}
