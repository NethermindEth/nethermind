// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.KeyStore.Test
{
    [Parallelizable(ParallelScope.All)]
    [TestFixture]
    public class KeyStoreTests
    {
        private class TestContext
        {
            public FileKeyStore Store { get; }
            public IJsonSerializer Serializer { get; }
            public IKeyStoreConfig KeyStoreConfig { get; }
            public ICryptoRandom CryptoRandom { get; }
            public SecureString TestPasswordSecured { get; }
            public SecureString WrongPasswordSecured { get; }
            public const string TestPassword = "testpassword";

            public TestContext()
            {
                KeyStoreConfig = new KeyStoreConfig();
                KeyStoreConfig.KeyStoreDirectory = NUnit.Framework.TestContext.CurrentContext.WorkDirectory;

                TestPasswordSecured = new SecureString();
                WrongPasswordSecured = new SecureString();

                for (int i = 0; i < TestPassword.Length; i++)
                {
                    TestPasswordSecured.AppendChar(TestPassword[i]);
                    WrongPasswordSecured.AppendChar('*');
                }

                TestPasswordSecured.MakeReadOnly();
                WrongPasswordSecured.MakeReadOnly();

                ILogManager logger = LimboLogs.Instance;
                Serializer = new EthereumJsonSerializer();
                CryptoRandom = new CryptoRandom();
                Store = new FileKeyStore(KeyStoreConfig, Serializer, new AesEncrypter(KeyStoreConfig, logger), CryptoRandom, logger, new PrivateKeyStoreIOSettingsProvider(KeyStoreConfig));
            }
        }

        [TestCase("{\"address\":\"20b2e4bb8688a44729780d15dc64adb42f9f5a0a\",\"crypto\":{\"cipher\":\"aes-128-ctr\",\"ciphertext\":\"d30cbb0f5b30ef86e57b7fa111307398b911b8c0a3eab4ac4edc4b2c8839afbe\",\"cipherparams\":{\"iv\":\"1e29e79023d73be3f3bb065ca9ddc078\"},\"kdf\":\"scrypt\",\"kdfparams\":{\"dklen\":32,\"n\":262144,\"p\":1,\"r\":8,\"salt\":\"fffcd979c3223b3cdfcb2cf21b07bd4313e6f8d02af8a79a5c5dc879a25680d3\"},\"mac\":\"3ac5a539775c33bd73adfd2c0d4ef8c9154e4b404e2a15c77b0e6c78cb90df20\"},\"id\":\"68462de1-4114-4f92-828b-883fae5f779c\",\"version\":3}")]
        [TestCase("{\"address\":\"746526c3a59db995b914a319306cd7ae35dc50c5\",\"crypto\":{\"cipher\":\"aes-128-ctr\",\"ciphertext\":\"644b1af45188b23f6195abd2b0563d7b079ff6622e5ac61767cd81cbd621a13e\",\"cipherparams\":{\"iv\":\"844c895835de8571409b8a76a75672b2\"},\"kdf\":\"scrypt\",\"kdfparams\":{\"dklen\":32,\"n\":262144,\"p\":1,\"r\":8,\"salt\":\"e7df76e322e444ed61314fa5261cf0ac02b9c057fe626a74a37c5255c16a8d61\"},\"mac\":\"48f26081eec397b818ed4e2cb3b1c04908c671a81d6b183e9965d869bd001862\"},\"id\":\"6ee56be1-367f-4b41-a25d-f60e0a7bfe42\",\"version\":3}")]
        [TestCase("{\"address\":\"aa42104423e00a862b616f2f712a1b17d308bbc9\",\"crypto\":{\"cipher\":\"aes-128-ctr\",\"ciphertext\":\"450c341ab64c39237039a30a8d84cc112dfbdda889caa19201b0cf8473680936\",\"cipherparams\":{\"iv\":\"923d950dcdba710a0c8e240441e0a227\"},\"kdf\":\"scrypt\",\"kdfparams\":{\"dklen\":32,\"n\":262144,\"p\":1,\"r\":8,\"salt\":\"101185ea81a1067591bce5323d75648b753f71becc22a4ebd55256593a705698\"},\"mac\":\"dc0a3bc555ac8f22d84115968b5fde6f50eb065ff7fe47a1da30de668a5ca864\"},\"id\":\"339ef573-a7d5-4bd0-86b2-3b1e420439d7\",\"version\":3}")]
        [TestCase("{\"address\":\"25dead29c683c5db3e0fabcf8f3757cdb0abe549\",\"crypto\":{\"cipher\":\"aes-128-ctr\",\"ciphertext\":\"4fd59f3a3fa1bed32774b29a40886d5489c0c06a8da014cb44b25792f6c32cb2\",\"cipherparams\":{\"iv\":\"6b850162043a0a879726839cfca55220\"},\"kdf\":\"scrypt\",\"kdfparams\":{\"dklen\":32,\"n\":262144,\"p\":1,\"r\":8,\"salt\":\"cafbe520e0d711cf32d9a2e6b2ecbd231cc7aed09018c5032c637436e02754d1\"},\"mac\":\"379f51c673f1f355a6ffc92b31b37381670eea2e0e23604a2572f5df650d148e\"},\"id\":\"fc7ff6bf-c51e-4e02-bb7c-0c91a3eeab4c\",\"version\":3}")]
        public void Can_unlock_test_accounts(string keyJson)
        {
            TestContext test = new TestContext();
            EthereumJsonSerializer serializer = new EthereumJsonSerializer();
            KeyStoreItem item = serializer.Deserialize<KeyStoreItem>(keyJson);

            SecureString securePassword = new SecureString();
            string password = "testpuppeth";
            for (int i = 0; i < password.Length; i++)
            {
                securePassword.AppendChar(password[i]);
            }

            securePassword.MakeReadOnly();

            test.Store.StoreKey(new Address(item.Address), item);
            try
            {
                (PrivateKey key, Result result) = test.Store.GetKey(new Address(item.Address), securePassword);
                Assert.That(result.ResultType, Is.EqualTo(ResultType.Success), item.Address + " " + result.Error);
                Assert.That(item.Address, Is.EqualTo(key.Address.ToString(false, false)));
            }
            finally
            {
                test.Store.DeleteKey(new Address(item.Address));
            }
        }

        [TestCase("{\"address\":\"20b2e4bb8688a44729780d15dc64adb42f9f5a0a\",\"crypto\":{\"cipher\":\"aes-128-ctr\",\"ciphertext\":\"d30cbb0f5b30ef86e57b7fa111307398b911b8c0a3eab4ac4edc4b2c8839afbe\",\"cipherparams\":{\"iv\":\"1e29e79023d73be3f3bb065ca9ddc078\"},\"kdf\":\"scrypt\",\"kdfparams\":{\"dklen\":32,\"n\":262144,\"p\":1,\"r\":8,\"salt\":\"fffcd979c3223b3cdfcb2cf21b07bd4313e6f8d02af8a79a5c5dc879a25680d3\"},\"mac\":\"3ac5a539775c33bd73adfd2c0d4ef8c9154e4b404e2a15c77b0e6c78cb90df20\"},\"id\":\"68462de1-4114-4f92-828b-883fae5f779c\",\"version\":3}", Ignore = "Order of fields changed from geth to mycryptowallet.")]
        [TestCase("{\"address\":\"746526c3a59db995b914a319306cd7ae35dc50c5\",\"crypto\":{\"cipher\":\"aes-128-ctr\",\"ciphertext\":\"644b1af45188b23f6195abd2b0563d7b079ff6622e5ac61767cd81cbd621a13e\",\"cipherparams\":{\"iv\":\"844c895835de8571409b8a76a75672b2\"},\"kdf\":\"scrypt\",\"kdfparams\":{\"dklen\":32,\"n\":262144,\"p\":1,\"r\":8,\"salt\":\"e7df76e322e444ed61314fa5261cf0ac02b9c057fe626a74a37c5255c16a8d61\"},\"mac\":\"48f26081eec397b818ed4e2cb3b1c04908c671a81d6b183e9965d869bd001862\"},\"id\":\"6ee56be1-367f-4b41-a25d-f60e0a7bfe42\",\"version\":3}", Ignore = "Order of fields changed from geth to mycryptowallet.")]
        [TestCase("{\"address\":\"aa42104423e00a862b616f2f712a1b17d308bbc9\",\"crypto\":{\"cipher\":\"aes-128-ctr\",\"ciphertext\":\"450c341ab64c39237039a30a8d84cc112dfbdda889caa19201b0cf8473680936\",\"cipherparams\":{\"iv\":\"923d950dcdba710a0c8e240441e0a227\"},\"kdf\":\"scrypt\",\"kdfparams\":{\"dklen\":32,\"n\":262144,\"p\":1,\"r\":8,\"salt\":\"101185ea81a1067591bce5323d75648b753f71becc22a4ebd55256593a705698\"},\"mac\":\"dc0a3bc555ac8f22d84115968b5fde6f50eb065ff7fe47a1da30de668a5ca864\"},\"id\":\"339ef573-a7d5-4bd0-86b2-3b1e420439d7\",\"version\":3}", Ignore = "Order of fields changed from geth to mycryptowallet.")]
        [TestCase("{\"address\":\"25dead29c683c5db3e0fabcf8f3757cdb0abe549\",\"crypto\":{\"cipher\":\"aes-128-ctr\",\"ciphertext\":\"4fd59f3a3fa1bed32774b29a40886d5489c0c06a8da014cb44b25792f6c32cb2\",\"cipherparams\":{\"iv\":\"6b850162043a0a879726839cfca55220\"},\"kdf\":\"scrypt\",\"kdfparams\":{\"dklen\":32,\"n\":262144,\"p\":1,\"r\":8,\"salt\":\"cafbe520e0d711cf32d9a2e6b2ecbd231cc7aed09018c5032c637436e02754d1\"},\"mac\":\"379f51c673f1f355a6ffc92b31b37381670eea2e0e23604a2572f5df650d148e\"},\"id\":\"fc7ff6bf-c51e-4e02-bb7c-0c91a3eeab4c\",\"version\":3}", Ignore = "Order of fields changed from geth to mycryptowallet.")]
        public void Same_storage_format_as_in_geth(string keyJson)
        {
            TestContext test = new TestContext();
            EthereumJsonSerializer serializer = new EthereumJsonSerializer();
            KeyStoreItem item = serializer.Deserialize<KeyStoreItem>(keyJson);

            SecureString securePassword = new SecureString();
            string password = "testpuppeth";
            for (int i = 0; i < password.Length; i++)
            {
                securePassword.AppendChar(password[i]);
            }

            Address address = new Address(item.Address);
            test.Store.StoreKey(address, item);

            try
            {
                string[] files = test.Store.FindKeyFiles(address);
                Assert.That(files.Length, Is.EqualTo(1));
                string text = File.ReadAllText(files[0]);
                Assert.That(text, Is.EqualTo(keyJson), "same json");
            }
            finally
            {
                test.Store.DeleteKey(new Address(item.Address));
            }
        }

        [Test]
        public void GenerateKeyAddressesTest()
        {
            TestContext test = new TestContext();

            PrivateKey key1;
            PrivateKey key2;

            string notAKeyPath = Path.Combine(test.KeyStoreConfig.KeyStoreDirectory, "not_a_key");

            using (var stream = File.Create(notAKeyPath))
            {
            }

            try
            {
                Result result;
                (key1, result) = test.Store.GenerateKey(test.TestPasswordSecured);
                Assert.That(result.ResultType, Is.EqualTo(ResultType.Success), "generate key 1");

                (key2, result) = test.Store.GenerateKey(test.TestPasswordSecured);
                Assert.That(result.ResultType, Is.EqualTo(ResultType.Success), "generate key 2");

                (IReadOnlyCollection<Address> addresses, Result getAllResult) = test.Store.GetKeyAddresses();
                Assert.That(getAllResult.ResultType, Is.EqualTo(ResultType.Success), "get key");
                Assert.IsTrue(addresses.Count() >= 2);
                Assert.IsNotNull(addresses.FirstOrDefault(x => x.Equals(key1.Address)), "key 1 not null");
                Assert.IsNotNull(addresses.FirstOrDefault(x => x.Equals(key2.Address)), "key 2 not null");

                result = test.Store.DeleteKey(key1.Address);
                Assert.That(result.ResultType, Is.EqualTo(ResultType.Success), "delete key 1");

                result = test.Store.DeleteKey(key2.Address);
                Assert.That(result.ResultType, Is.EqualTo(ResultType.Success), "delete key 2");
            }
            finally
            {
                File.Delete(notAKeyPath);
            }
        }

        [Test]
        public void GenerateKeyTest()
        {
            TestContext test = new TestContext();
            (PrivateKey, Result) key = test.Store.GenerateKey(test.TestPasswordSecured);
            Assert.That(key.Item2.ResultType, Is.EqualTo(ResultType.Success));

            (PrivateKey, Result) persistedKey = test.Store.GetKey(key.Item1.Address, test.TestPasswordSecured);
            Assert.That(persistedKey.Item2.ResultType, Is.EqualTo(ResultType.Success));
            Assert.IsTrue(Bytes.AreEqual(key.Item1.KeyBytes, persistedKey.Item1.KeyBytes));

            Result result = test.Store.DeleteKey(key.Item1.Address);
            Assert.That(result.ResultType, Is.EqualTo(ResultType.Success));

            (PrivateKey, Result) deletedKey = test.Store.GetKey(key.Item1.Address, test.TestPasswordSecured);
            Assert.That(deletedKey.Item2.ResultType, Is.EqualTo(ResultType.Failure));
        }

        [Test]
        public void Salt32Test()
        {
            TestContext test = new TestContext();
            (PrivateKey, Result) key = test.Store.GenerateKey(test.TestPasswordSecured);
            Assert.That(key.Item2.ResultType, Is.EqualTo(ResultType.Success));

            (PrivateKey, Result) persistedKey = test.Store.GetKey(key.Item1.Address, test.TestPasswordSecured);
            Assert.That(persistedKey.Item2.ResultType, Is.EqualTo(ResultType.Success));
            Assert.IsTrue(Bytes.AreEqual(key.Item1.KeyBytes, persistedKey.Item1.KeyBytes));

            Result result = test.Store.DeleteKey(key.Item1.Address);
            Assert.That(result.ResultType, Is.EqualTo(ResultType.Success));

            (PrivateKey, Result) deletedKey = test.Store.GetKey(key.Item1.Address, test.TestPasswordSecured);
            Assert.That(deletedKey.Item2.ResultType, Is.EqualTo(ResultType.Failure));
        }

        [Test]
        public void KeyStoreVersionMismatchTest()
        {
            TestContext test = new TestContext();
            //generate key
            (PrivateKey key, Result storeResult) = test.Store.GenerateKey(test.TestPasswordSecured);
            Assert.That(storeResult.ResultType, Is.EqualTo(ResultType.Success), "generate result");

            (KeyStoreItem keyData, Result result) = test.Store.GetKeyData(key.Address);
            Assert.That(result.ResultType, Is.EqualTo(ResultType.Success), "load result");

            Result deleteResult = test.Store.DeleteKey(key.Address);
            Assert.That(deleteResult.ResultType, Is.EqualTo(ResultType.Success), "delete result");

            keyData.Version = 0;
            test.Store.StoreKey(key.Address, keyData);

            (_, Result loadResult) = test.Store.GetKey(key.Address, test.TestPasswordSecured);
            Assert.That(loadResult.ResultType, Is.EqualTo(ResultType.Failure), "bad load result");
            Assert.That(loadResult.Error, Is.EqualTo("KeyStore version mismatch"));

            test.Store.DeleteKey(key.Address);
        }

        [Test]
        public void WrongPasswordTest()
        {
            TestContext test = new TestContext();
            (PrivateKey key, Result generateResult) = test.Store.GenerateKey(test.TestPasswordSecured);
            Assert.That(generateResult.ResultType, Is.EqualTo(ResultType.Success));

            (PrivateKey keyRestored, Result getResult) = test.Store.GetKey(key.Address, test.TestPasswordSecured);
            Assert.That(getResult.ResultType, Is.EqualTo(ResultType.Success));
            Assert.IsTrue(Bytes.AreEqual(key.KeyBytes, keyRestored.KeyBytes));

            (PrivateKey _, Result wrongResult) = test.Store.GetKey(key.Address, test.WrongPasswordSecured);
            Assert.That(wrongResult.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(wrongResult.Error, Is.EqualTo("Incorrect MAC"));

            Result deleteResult = test.Store.DeleteKey(key.Address);
            Assert.That(deleteResult.ResultType, Is.EqualTo(ResultType.Success));
        }

        [Test]
        public void ShouldSaveFileWithoutBom()
        {
            TestContext test = new TestContext();
            const string bomBytesHex = "efbbbf";
            const string validBytesHex = "7b2276";
            var (key, _) = test.Store.GenerateKey(test.TestPasswordSecured);
            var directory = test.KeyStoreConfig.KeyStoreDirectory.GetApplicationResourcePath();
            var addressHex = key.Address.ToString(false, false);
            var file = Directory.GetFiles(directory).SingleOrDefault(f => f.Contains(addressHex));
            var bytes = File.ReadAllBytes(file);
            test.Store.DeleteKey(key.Address);
            var bytesHex = bytes.ToHexString();
            bytesHex.Should().NotStartWith(bomBytesHex);
            bytesHex.Should().StartWith(validBytesHex);
        }
    }
}
