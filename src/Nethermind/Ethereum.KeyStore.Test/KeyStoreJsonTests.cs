// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Text.Json.Serialization;

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Crypto;
using Nethermind.KeyStore;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

using NUnit.Framework;

namespace Ethereum.KeyStore.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class KeyStoreJsonTests
    {
        private IKeyStore _store;
        private IJsonSerializer _serializer;
        private ICryptoRandom _cryptoRandom;
        private Dictionary<string, KeyStoreTestModel> _testsModel;

        [SetUp]
        public void Initialize()
        {
            IKeyStoreConfig config = new KeyStoreConfig
            {
                KeyStoreDirectory = TestContext.CurrentContext.WorkDirectory
            };

            if (!Directory.Exists(config.KeyStoreDirectory))
                Directory.CreateDirectory(config.KeyStoreDirectory);

            ILogManager logManager = LimboLogs.Instance;
            _serializer = new EthereumJsonSerializer();
            _cryptoRandom = new CryptoRandom();
            _store = new FileKeyStore(config, _serializer, new AesEncrypter(config, logManager), _cryptoRandom, logManager, new PrivateKeyStoreIOSettingsProvider(config));

            string testsContent = File.ReadAllText("basic_tests.json");
            _testsModel = _serializer.Deserialize<Dictionary<string, KeyStoreTestModel>>(testsContent);
        }

        [TearDown]
        public void TearDown() => _cryptoRandom?.Dispose();

        [TestCase("test1")]
        [TestCase("test2")]
        [TestCase("python_generated_test_with_odd_iv")]
        [TestCase("evilnonce")]
        [TestCase("mycrypto")]
        public void Test(string testName)
        {
            KeyStoreTestModel testModel = _testsModel[testName];
            testModel.KeyData.Address = testModel.Address ?? new PrivateKey(testModel.Priv).Address.ToString(false, false);
            Address address = new(testModel.KeyData.Address);
            _store.StoreKey(address, testModel.KeyData);

            try
            {
                SecureString securedPass = new();
                testModel.Password.ToCharArray().ForEach(x => securedPass.AppendChar(x));
                securedPass.MakeReadOnly();
                (PrivateKey key, Result result) = _store.GetKey(address, securedPass);

                Assert.That(result.ResultType, Is.EqualTo(ResultType.Success), result.Error);
                Assert.That(key.Address.ToString(false, false), Is.EqualTo(testModel.KeyData.Address));
            }
            catch (Exception e)
            {
                Assert.Fail(
                    "Exception during test execution." + "\n" +
                    "Message: " + e.Message + "\n" +
                    "Source: " + e.Source + "\n" +
                    "InnerException: " + e.InnerException);
            }
            finally
            {
                _store.DeleteKey(address);
            }
        }

        private class KeyStoreTestModel
        {
            [JsonPropertyName("Json")]
            public KeyStoreItem KeyData { get; set; }
            public string Password { get; set; }
            public string Priv { get; set; }

            public string Address { get; set; }
        }
    }
}
