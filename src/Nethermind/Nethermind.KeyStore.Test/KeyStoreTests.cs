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

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.KeyStore.Config;
using NUnit.Framework;

namespace Nethermind.KeyStore.Test
{
    [TestFixture]
    [Ignore("Need to use proper Scrypt")]
    public class KeyStoreTests
    {
        private IKeyStore _store;
        private IJsonSerializer _serializer;
        private IConfigProvider _configurationProvider;
        private ICryptoRandom _cryptoRandom;
        private SecureString _testPasswordSecured;
        private SecureString _wrongPasswordSecured;
        private readonly string _testPassword = "testpassword";

        [SetUp]
        public void Initialize()
        {
            _testPasswordSecured = new SecureString();
            _wrongPasswordSecured = new SecureString();

            for (int i = 0; i < _testPassword.Length; i++)
            {
                _testPasswordSecured.AppendChar(_testPassword[i]);
                _wrongPasswordSecured.AppendChar('*');
            }

            _testPasswordSecured.MakeReadOnly();
            _wrongPasswordSecured.MakeReadOnly();

            _configurationProvider = new JsonConfigProvider();

            ILogManager logger = NullLogManager.Instance;
            _serializer = new JsonSerializer(logger);
            _cryptoRandom = new CryptoRandom();
            _store = new FileKeyStore(_configurationProvider, _serializer, new AesEncrypter(_configurationProvider, logger), _cryptoRandom, logger);
        }

        [Test]
        public void CryptoVersionMismatchTest()
        {
            //generate key
            (PrivateKey, Result) key = _store.GenerateKey(_testPasswordSecured);
            Assert.AreEqual(ResultType.Success, key.Item2.ResultType);

            //replace version
            string filePath = Path.Combine(_configurationProvider.GetConfig<IKeystoreConfig>().KeyStoreDirectory, key.Item1.Address.ToString());
            KeyStoreItem item = _serializer.Deserialize<KeyStoreItem>(File.ReadAllText(filePath));
            item.Crypto.Version = 0;
            string json = _serializer.Serialize(item);
            File.WriteAllText(filePath, json);

            //try to read
            (PrivateKey, Result) key2 = _store.GetKey(key.Item1.Address, _testPasswordSecured);
            Assert.AreEqual(ResultType.Failure, key2.Item2.ResultType);
            Assert.AreEqual("Crypto version mismatch", key2.Item2.Error);

            //clean up
            File.Delete(filePath);
        }

        [Test]
        public void GenerateKeyAddressesTest()
        {
            Result result;
            PrivateKey key1;
            PrivateKey key2;

            File.Create(Path.Combine(_configurationProvider.GetConfig<IKeystoreConfig>().KeyStoreDirectory, "not_a_key"));
            
            (key1, result) = _store.GenerateKey(_testPasswordSecured);
            Assert.AreEqual(ResultType.Success, result.ResultType, "generate key 1");

            (key2, result) = _store.GenerateKey(_testPasswordSecured);
            Assert.AreEqual(ResultType.Success, result.ResultType, "generate key 2");

            //get key addreses
            (IReadOnlyCollection<Address> addresses, Result getAllResult) = _store.GetKeyAddresses();
            Assert.AreEqual(ResultType.Success, getAllResult.ResultType, "get key");
            Assert.IsTrue(addresses.Count() >= 2);
            Assert.IsNotNull(addresses.FirstOrDefault(x => x.Equals(key1.Address)), "key 1 not null");
            Assert.IsNotNull(addresses.FirstOrDefault(x => x.Equals(key2.Address)), "key 2 not null");

            //delete generated keys
            result = _store.DeleteKey(key1.Address, _testPasswordSecured);
            Assert.AreEqual(ResultType.Success, result.ResultType, "delete key 1");

            result = _store.DeleteKey(key2.Address, _testPasswordSecured);
            Assert.AreEqual(ResultType.Success, result.ResultType, "delete key 2");
        }

        [Test]
        public void GenerateKeyTest()
        {
            //generate key
            (PrivateKey, Result) key = _store.GenerateKey(_testPasswordSecured);
            Assert.AreEqual(ResultType.Success, key.Item2.ResultType);

            //get persisted key, verify it matches generated key
            (PrivateKey, Result) persistedKey = _store.GetKey(key.Item1.Address, _testPasswordSecured);
            Assert.AreEqual(ResultType.Success, persistedKey.Item2.ResultType);
            Assert.IsTrue(Bytes.AreEqual(key.Item1.KeyBytes, persistedKey.Item1.KeyBytes));

            //delete generated key
            Result result = _store.DeleteKey(key.Item1.Address, _testPasswordSecured);
            Assert.AreEqual(ResultType.Success, result.ResultType);

            //get created key, verify it does not exist anymore
            (PrivateKey, Result) deletedKey = _store.GetKey(key.Item1.Address, _testPasswordSecured);
            Assert.AreEqual(ResultType.Failure, deletedKey.Item2.ResultType);
        }

        [Test]
        public void KeyStoreVersionMismatchTest()
        {
            //generate key
            (PrivateKey, Result) key = _store.GenerateKey(_testPasswordSecured);
            Assert.AreEqual(ResultType.Success, key.Item2.ResultType);

            //replace version
            string filePath = Path.Combine(_configurationProvider.GetConfig<IKeystoreConfig>().KeyStoreDirectory, key.Item1.Address.ToString());
            KeyStoreItem item = _serializer.Deserialize<KeyStoreItem>(File.ReadAllText(filePath));
            item.Version = 1;
            string json = _serializer.Serialize(item);
            File.WriteAllText(filePath, json);

            //try to read
            (PrivateKey, Result) key2 = _store.GetKey(key.Item1.Address, _testPasswordSecured);
            Assert.AreEqual(ResultType.Failure, key2.Item2.ResultType);
            Assert.AreEqual("KeyStore version mismatch", key2.Item2.Error);

            //clean up
            File.Delete(filePath);
        }

        [Test]
        public void WrongPasswordTest()
        {
            //generate key
            (PrivateKey, Result) key = _store.GenerateKey(_testPasswordSecured);
            Assert.AreEqual(ResultType.Success, key.Item2.ResultType);

            //Get with right pass
            (PrivateKey, Result) key2 = _store.GetKey(key.Item1.Address, _testPasswordSecured);
            Assert.AreEqual(ResultType.Success, key2.Item2.ResultType);
            Assert.IsTrue(Bytes.AreEqual(key.Item1.KeyBytes, key2.Item1.KeyBytes));

            //Try to Get with wrong pass
            (PrivateKey, Result) key3 = _store.GetKey(key.Item1.Address, _wrongPasswordSecured);
            Assert.AreEqual(ResultType.Failure, key3.Item2.ResultType);
            Assert.AreEqual("Incorrect MAC", key3.Item2.Error);

            //delete generated key
            Result result = _store.DeleteKey(key.Item1.Address, _testPasswordSecured);
            Assert.AreEqual(ResultType.Success, result.ResultType);
        }
    }
}