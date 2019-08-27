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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Json;
using Nethermind.Core.Model;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.KeyStore.Test
{
    [TestFixture]
    public class KeyStoreTests
    {
        private FileKeyStore _store;
        private IJsonSerializer _serializer;
        private IKeyStoreConfig _keyStoreConfig;
        private ICryptoRandom _cryptoRandom;
        private SecureString _testPasswordSecured;
        private SecureString _wrongPasswordSecured;
        private readonly string _testPassword = "testpassword";

        [SetUp]
        public void Initialize()
        {
            _keyStoreConfig = new KeyStoreConfig();

            _testPasswordSecured = new SecureString();
            _wrongPasswordSecured = new SecureString();

            for (int i = 0; i < _testPassword.Length; i++)
            {
                _testPasswordSecured.AppendChar(_testPassword[i]);
                _wrongPasswordSecured.AppendChar('*');
            }

            _testPasswordSecured.MakeReadOnly();
            _wrongPasswordSecured.MakeReadOnly();

            ILogManager logger = NullLogManager.Instance;
            _serializer = new EthereumJsonSerializer();
            _cryptoRandom = new CryptoRandom();
            _store = new FileKeyStore(_keyStoreConfig, _serializer, new AesEncrypter(_keyStoreConfig, logger), _cryptoRandom, logger);
        }

        [TestCase("{\"address\":\"20b2e4bb8688a44729780d15dc64adb42f9f5a0a\",\"crypto\":{\"cipher\":\"aes-128-ctr\",\"ciphertext\":\"d30cbb0f5b30ef86e57b7fa111307398b911b8c0a3eab4ac4edc4b2c8839afbe\",\"cipherparams\":{\"iv\":\"1e29e79023d73be3f3bb065ca9ddc078\"},\"kdf\":\"scrypt\",\"kdfparams\":{\"dklen\":32,\"n\":262144,\"p\":1,\"r\":8,\"salt\":\"fffcd979c3223b3cdfcb2cf21b07bd4313e6f8d02af8a79a5c5dc879a25680d3\"},\"mac\":\"3ac5a539775c33bd73adfd2c0d4ef8c9154e4b404e2a15c77b0e6c78cb90df20\"},\"id\":\"68462de1-4114-4f92-828b-883fae5f779c\",\"version\":3}")]
        [TestCase("{\"address\":\"746526c3a59db995b914a319306cd7ae35dc50c5\",\"crypto\":{\"cipher\":\"aes-128-ctr\",\"ciphertext\":\"644b1af45188b23f6195abd2b0563d7b079ff6622e5ac61767cd81cbd621a13e\",\"cipherparams\":{\"iv\":\"844c895835de8571409b8a76a75672b2\"},\"kdf\":\"scrypt\",\"kdfparams\":{\"dklen\":32,\"n\":262144,\"p\":1,\"r\":8,\"salt\":\"e7df76e322e444ed61314fa5261cf0ac02b9c057fe626a74a37c5255c16a8d61\"},\"mac\":\"48f26081eec397b818ed4e2cb3b1c04908c671a81d6b183e9965d869bd001862\"},\"id\":\"6ee56be1-367f-4b41-a25d-f60e0a7bfe42\",\"version\":3}")]
        [TestCase("{\"address\":\"aa42104423e00a862b616f2f712a1b17d308bbc9\",\"crypto\":{\"cipher\":\"aes-128-ctr\",\"ciphertext\":\"450c341ab64c39237039a30a8d84cc112dfbdda889caa19201b0cf8473680936\",\"cipherparams\":{\"iv\":\"923d950dcdba710a0c8e240441e0a227\"},\"kdf\":\"scrypt\",\"kdfparams\":{\"dklen\":32,\"n\":262144,\"p\":1,\"r\":8,\"salt\":\"101185ea81a1067591bce5323d75648b753f71becc22a4ebd55256593a705698\"},\"mac\":\"dc0a3bc555ac8f22d84115968b5fde6f50eb065ff7fe47a1da30de668a5ca864\"},\"id\":\"339ef573-a7d5-4bd0-86b2-3b1e420439d7\",\"version\":3}")]
        [TestCase("{\"address\":\"25dead29c683c5db3e0fabcf8f3757cdb0abe549\",\"crypto\":{\"cipher\":\"aes-128-ctr\",\"ciphertext\":\"4fd59f3a3fa1bed32774b29a40886d5489c0c06a8da014cb44b25792f6c32cb2\",\"cipherparams\":{\"iv\":\"6b850162043a0a879726839cfca55220\"},\"kdf\":\"scrypt\",\"kdfparams\":{\"dklen\":32,\"n\":262144,\"p\":1,\"r\":8,\"salt\":\"cafbe520e0d711cf32d9a2e6b2ecbd231cc7aed09018c5032c637436e02754d1\"},\"mac\":\"379f51c673f1f355a6ffc92b31b37381670eea2e0e23604a2572f5df650d148e\"},\"id\":\"fc7ff6bf-c51e-4e02-bb7c-0c91a3eeab4c\",\"version\":3}")]
        public void Can_unlock_test_accounts(string keyJson)
        {
            EthereumJsonSerializer serializer = new EthereumJsonSerializer();
            KeyStoreItem item = serializer.Deserialize<KeyStoreItem>(keyJson);

            SecureString securePassword = new SecureString();
            string password = "testpuppeth";
            for (int i = 0; i < password.Length; i++)
            {
                securePassword.AppendChar(password[i]);
            }
            
            securePassword.MakeReadOnly();

            _store.StoreKey(new Address(item.Address), item);
            try
            {
                (PrivateKey key, Result result) = _store.GetKey(new Address(item.Address), securePassword);
                Assert.AreEqual(ResultType.Success, result.ResultType, item.Address + " " + result.Error);
                Assert.AreEqual(key.Address.ToString(false, false), item.Address);
            }
            finally
            {
                _store.DeleteKey(new Address(item.Address));
            }
        }

        [TestCase("{\"address\":\"20b2e4bb8688a44729780d15dc64adb42f9f5a0a\",\"crypto\":{\"cipher\":\"aes-128-ctr\",\"ciphertext\":\"d30cbb0f5b30ef86e57b7fa111307398b911b8c0a3eab4ac4edc4b2c8839afbe\",\"cipherparams\":{\"iv\":\"1e29e79023d73be3f3bb065ca9ddc078\"},\"kdf\":\"scrypt\",\"kdfparams\":{\"dklen\":32,\"n\":262144,\"p\":1,\"r\":8,\"salt\":\"fffcd979c3223b3cdfcb2cf21b07bd4313e6f8d02af8a79a5c5dc879a25680d3\"},\"mac\":\"3ac5a539775c33bd73adfd2c0d4ef8c9154e4b404e2a15c77b0e6c78cb90df20\"},\"id\":\"68462de1-4114-4f92-828b-883fae5f779c\",\"version\":3}", Ignore="Order of fields changed from geth to mycryptowallet.")]
        [TestCase("{\"address\":\"746526c3a59db995b914a319306cd7ae35dc50c5\",\"crypto\":{\"cipher\":\"aes-128-ctr\",\"ciphertext\":\"644b1af45188b23f6195abd2b0563d7b079ff6622e5ac61767cd81cbd621a13e\",\"cipherparams\":{\"iv\":\"844c895835de8571409b8a76a75672b2\"},\"kdf\":\"scrypt\",\"kdfparams\":{\"dklen\":32,\"n\":262144,\"p\":1,\"r\":8,\"salt\":\"e7df76e322e444ed61314fa5261cf0ac02b9c057fe626a74a37c5255c16a8d61\"},\"mac\":\"48f26081eec397b818ed4e2cb3b1c04908c671a81d6b183e9965d869bd001862\"},\"id\":\"6ee56be1-367f-4b41-a25d-f60e0a7bfe42\",\"version\":3}", Ignore="Order of fields changed from geth to mycryptowallet.")]
        [TestCase("{\"address\":\"aa42104423e00a862b616f2f712a1b17d308bbc9\",\"crypto\":{\"cipher\":\"aes-128-ctr\",\"ciphertext\":\"450c341ab64c39237039a30a8d84cc112dfbdda889caa19201b0cf8473680936\",\"cipherparams\":{\"iv\":\"923d950dcdba710a0c8e240441e0a227\"},\"kdf\":\"scrypt\",\"kdfparams\":{\"dklen\":32,\"n\":262144,\"p\":1,\"r\":8,\"salt\":\"101185ea81a1067591bce5323d75648b753f71becc22a4ebd55256593a705698\"},\"mac\":\"dc0a3bc555ac8f22d84115968b5fde6f50eb065ff7fe47a1da30de668a5ca864\"},\"id\":\"339ef573-a7d5-4bd0-86b2-3b1e420439d7\",\"version\":3}", Ignore="Order of fields changed from geth to mycryptowallet.")]
        [TestCase("{\"address\":\"25dead29c683c5db3e0fabcf8f3757cdb0abe549\",\"crypto\":{\"cipher\":\"aes-128-ctr\",\"ciphertext\":\"4fd59f3a3fa1bed32774b29a40886d5489c0c06a8da014cb44b25792f6c32cb2\",\"cipherparams\":{\"iv\":\"6b850162043a0a879726839cfca55220\"},\"kdf\":\"scrypt\",\"kdfparams\":{\"dklen\":32,\"n\":262144,\"p\":1,\"r\":8,\"salt\":\"cafbe520e0d711cf32d9a2e6b2ecbd231cc7aed09018c5032c637436e02754d1\"},\"mac\":\"379f51c673f1f355a6ffc92b31b37381670eea2e0e23604a2572f5df650d148e\"},\"id\":\"fc7ff6bf-c51e-4e02-bb7c-0c91a3eeab4c\",\"version\":3}", Ignore="Order of fields changed from geth to mycryptowallet.")]
        public void Same_storage_format_as_in_geth(string keyJson)
        {
            EthereumJsonSerializer serializer = new EthereumJsonSerializer();
            KeyStoreItem item = serializer.Deserialize<KeyStoreItem>(keyJson);

            SecureString securePassword = new SecureString();
            string password = "testpuppeth";
            for (int i = 0; i < password.Length; i++)
            {
                securePassword.AppendChar(password[i]);
            }

            Address address = new Address(item.Address);
            _store.StoreKey(address, item);
            
            try
            {
                string[] files = _store.FindKeyFiles(address);
                Assert.AreEqual(1, files.Length);
                string text = File.ReadAllText(files[0]);
                Assert.AreEqual(keyJson, text, "same json");
            }
            finally
            {
                _store.DeleteKey(new Address(item.Address));
            }
        }

        [Test]
        public void GenerateKeyAddressesTest()
        {
            PrivateKey key1;
            PrivateKey key2;

            string notAKeyPath = Path.Combine(_keyStoreConfig.KeyStoreDirectory, "not_a_key");

            using (var stream = File.Create(notAKeyPath))
            {
            }

            try
            {
                Result result;
                (key1, result) = _store.GenerateKey(_testPasswordSecured);
                Assert.AreEqual(ResultType.Success, result.ResultType, "generate key 1");

                (key2, result) = _store.GenerateKey(_testPasswordSecured);
                Assert.AreEqual(ResultType.Success, result.ResultType, "generate key 2");

                (IReadOnlyCollection<Address> addresses, Result getAllResult) = _store.GetKeyAddresses();
                Assert.AreEqual(ResultType.Success, getAllResult.ResultType, "get key");
                Assert.IsTrue(addresses.Count() >= 2);
                Assert.IsNotNull(addresses.FirstOrDefault(x => x.Equals(key1.Address)), "key 1 not null");
                Assert.IsNotNull(addresses.FirstOrDefault(x => x.Equals(key2.Address)), "key 2 not null");

                result = _store.DeleteKey(key1.Address);
                Assert.AreEqual(ResultType.Success, result.ResultType, "delete key 1");

                result = _store.DeleteKey(key2.Address);
                Assert.AreEqual(ResultType.Success, result.ResultType, "delete key 2");
            }
            finally
            {
                File.Delete(notAKeyPath);
            }
        }

        [Test]
        public void GenerateKeyTest()
        {
            (PrivateKey, Result) key = _store.GenerateKey(_testPasswordSecured);
            Assert.AreEqual(ResultType.Success, key.Item2.ResultType);

            (PrivateKey, Result) persistedKey = _store.GetKey(key.Item1.Address, _testPasswordSecured);
            Assert.AreEqual(ResultType.Success, persistedKey.Item2.ResultType);
            Assert.IsTrue(Bytes.AreEqual(key.Item1.KeyBytes, persistedKey.Item1.KeyBytes));

            Result result = _store.DeleteKey(key.Item1.Address);
            Assert.AreEqual(ResultType.Success, result.ResultType);

            (PrivateKey, Result) deletedKey = _store.GetKey(key.Item1.Address, _testPasswordSecured);
            Assert.AreEqual(ResultType.Failure, deletedKey.Item2.ResultType);
        }

        [Test]
        public void Salt32Test()
        {
            (PrivateKey, Result) key = _store.GenerateKey(_testPasswordSecured);
            Assert.AreEqual(ResultType.Success, key.Item2.ResultType);

            (PrivateKey, Result) persistedKey = _store.GetKey(key.Item1.Address, _testPasswordSecured);
            Assert.AreEqual(ResultType.Success, persistedKey.Item2.ResultType);
            Assert.IsTrue(Bytes.AreEqual(key.Item1.KeyBytes, persistedKey.Item1.KeyBytes));

            Result result = _store.DeleteKey(key.Item1.Address);
            Assert.AreEqual(ResultType.Success, result.ResultType);

            (PrivateKey, Result) deletedKey = _store.GetKey(key.Item1.Address, _testPasswordSecured);
            Assert.AreEqual(ResultType.Failure, deletedKey.Item2.ResultType);
        }

        [Test]
        public void KeyStoreVersionMismatchTest()
        {
            //generate key
            (PrivateKey key, Result storeResult) = _store.GenerateKey(_testPasswordSecured);
            Assert.AreEqual(ResultType.Success, storeResult.ResultType, "generate result");

            (KeyStoreItem keyData, Result result) = _store.GetKeyData(key.Address);
            Assert.AreEqual(ResultType.Success, result.ResultType, "load result");

            Result deleteResult = _store.DeleteKey(key.Address);
            Assert.AreEqual(ResultType.Success, deleteResult.ResultType, "delete result");

            keyData.Version = 0;
            _store.StoreKey(key.Address, keyData);

            (_, Result loadResult) = _store.GetKey(key.Address, _testPasswordSecured);
            Assert.AreEqual(ResultType.Failure, loadResult.ResultType, "bad load result");
            Assert.AreEqual("KeyStore version mismatch", loadResult.Error);

            _store.DeleteKey(key.Address);
        }

        [Test]
        public void WrongPasswordTest()
        {
            (PrivateKey key, Result generateResult) = _store.GenerateKey(_testPasswordSecured);
            Assert.AreEqual(ResultType.Success, generateResult.ResultType);

            (PrivateKey keyRestored, Result getResult) = _store.GetKey(key.Address, _testPasswordSecured);
            Assert.AreEqual(ResultType.Success, getResult.ResultType);
            Assert.IsTrue(Bytes.AreEqual(key.KeyBytes, keyRestored.KeyBytes));

            (PrivateKey _, Result wrongResult) = _store.GetKey(key.Address, _wrongPasswordSecured);
            Assert.AreEqual(ResultType.Failure, wrongResult.ResultType);
            Assert.AreEqual("Incorrect MAC", wrongResult.Error);

            Result deleteResult = _store.DeleteKey(key.Address);
            Assert.AreEqual(ResultType.Success, deleteResult.ResultType);
        }
    }
}