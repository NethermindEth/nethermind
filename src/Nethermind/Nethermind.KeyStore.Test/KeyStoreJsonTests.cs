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

using System.IO;
using System.Linq;
using System.Security;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Json;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.Core.Test.Builders;
using Nethermind.KeyStore.Config;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Nethermind.KeyStore.Test
{
    [TestFixture]
    public class KeyStoreJsonTests
    {
        private IKeyStore _store;
        private IJsonSerializer _serializer;
        private IConfigProvider _configurationProvider;
        private ICryptoRandom _cryptoRandom;
        private string _keyStoreDir;
        private KeyStoreTestsModel _testsModel;

        [SetUp]
        public void Initialize()
        {
            _configurationProvider = new ConfigProvider();
            _keyStoreDir = _configurationProvider.GetConfig<IKeystoreConfig>().KeyStoreDirectory;
            if (!Directory.Exists(_keyStoreDir))
            {
                Directory.CreateDirectory(_keyStoreDir);
            }

            ILogManager logManager = NullLogManager.Instance;
            _serializer = new EthereumJsonSerializer();
            _cryptoRandom = new CryptoRandom();
            _store = new FileKeyStore(_configurationProvider, _serializer, new AesEncrypter(_configurationProvider, logManager), _cryptoRandom, logManager);

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

        private void RunTest(KeyStoreTestModel testModel)
        {
            //this is missed in json file
            testModel.KeyData.Crypto.Version = 1;
            testModel.KeyData.Address = new PrivateKey(testModel.Priv).Address.ToString(false, false);
            Address address = new Address(testModel.KeyData.Address);
            _store.StoreKey(address, testModel.KeyData);
            
            var securedPass = new SecureString();
            testModel.Password.ToCharArray().ToList().ForEach(x => securedPass.AppendChar(x));
            var key = _store.GetKey(address, securedPass);

            //verify private key
            Assert.AreEqual(ResultType.Success, key.Result.ResultType);
            Assert.AreEqual(new PrivateKey(Bytes.FromHexString(testModel.Priv)), key.PrivateKey);

            //clean up
            _store.DeleteKey(address);
        }

        private class KeyStoreTestsModel
        {
            public KeyStoreTestModel Test1 { get; set; }
            public KeyStoreTestModel Test2 { get; set; }
            public KeyStoreTestModel Python_generated_test_with_odd_iv { get; set; }
            public KeyStoreTestModel EvilNonce { get; set; }
        }

        private class KeyStoreTestModel
        {
            [JsonProperty(PropertyName = "Json")]
            public KeyStoreItem KeyData { get; set; }
            public string Password { get; set; }
            public string Priv { get; set; }
        }
    } 
}