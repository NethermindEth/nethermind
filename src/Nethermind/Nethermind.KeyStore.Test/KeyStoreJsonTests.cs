// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json.Serialization;

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Crypto;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

using NUnit.Framework;

namespace Nethermind.KeyStore.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class KeyStoreJsonTests
    {
        private IKeyStore _store;
        private IJsonSerializer _serializer;
        private IKeyStoreConfig _config;
        private ICryptoRandom _cryptoRandom;
        private string _keyStoreDir;
        private KeyStoreTestsModel _testsModel;

        [SetUp]
        public void Initialize()
        {
            _config = new KeyStoreConfig();
            _config.KeyStoreDirectory = TestContext.CurrentContext.WorkDirectory;

            _keyStoreDir = _config.KeyStoreDirectory;
            if (!Directory.Exists(_keyStoreDir))
            {
                Directory.CreateDirectory(_keyStoreDir);
            }

            ILogManager logManager = LimboLogs.Instance;
            _serializer = new EthereumJsonSerializer();
            _cryptoRandom = new CryptoRandom();
            _store = new FileKeyStore(_config, _serializer, new AesEncrypter(_config, logManager), _cryptoRandom, logManager, new PrivateKeyStoreIOSettingsProvider(_config));

            var testsContent = File.ReadAllText("basic_tests.json");
            _testsModel = _serializer.Deserialize<KeyStoreTestsModel>(testsContent);
        }

        [Test]
        public void Test1Test()
        {
            var testModel = _testsModel.Test1;
            RunTest(testModel);
        }

        [Test]
        public void Test2Test()
        {
            var testModel = _testsModel.Test2;
            RunTest(testModel);
        }

        [Test]
        public void OddIvTest()
        {
            var testModel = _testsModel.Python_generated_test_with_odd_iv;
            RunTest(testModel);
        }

        [Test]
        public void EvilNonceTest()
        {
            var testModel = _testsModel.EvilNonce;
            RunTest(testModel);
        }

        [Test]
        public void MyCryptoTest()
        {
            var testModel = _testsModel.MyCrypto;
            RunTest(testModel);
        }

        private void RunTest(KeyStoreTestModel testModel)
        {
            testModel.KeyData.Address = testModel.Address ?? new PrivateKey(testModel.Priv).Address.ToString(false, false);
            Address address = new Address(testModel.KeyData.Address);
            _store.StoreKey(address, testModel.KeyData);

            try
            {
                var securedPass = new SecureString();
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

        private class KeyStoreTestsModel
        {
            public KeyStoreTestModel Test1 { get; set; }
            public KeyStoreTestModel Test2 { get; set; }
            public KeyStoreTestModel Python_generated_test_with_odd_iv { get; set; }
            public KeyStoreTestModel EvilNonce { get; set; }
            public KeyStoreTestModel MyCrypto { get; set; }

            public KeyStoreTestModel Sealer0 { get; set; }
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
