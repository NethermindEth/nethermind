//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.IO;
using System.Linq;
using System.Security;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;
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
                testModel.Password.ToCharArray().ToList().ForEach(x => securedPass.AppendChar(x));
                securedPass.MakeReadOnly();
                (PrivateKey key, Result result) = _store.GetKey(address, securedPass);

                Assert.AreEqual(ResultType.Success, result.ResultType, result.Error);
                Assert.AreEqual(testModel.KeyData.Address, key.Address.ToString(false, false));

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
            [JsonProperty(PropertyName = "Json")]
            public KeyStoreItem KeyData { get; set; }
            public string Password { get; set; }
            public string Priv { get; set; }
            
            public string Address { get; set; }
        }
    } 
}
