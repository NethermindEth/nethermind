using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.Core.Test.Builders;
using Nethermind.KeyStore.Config;
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
        private Address _testAddress;

        [SetUp]
        public void Initialize()
        {
            _configurationProvider = new JsonConfigProvider();
            _keyStoreDir = _configurationProvider.GetConfig<IKeystoreConfig>().KeyStoreDirectory;
            if (!Directory.Exists(_keyStoreDir))
            {
                Directory.CreateDirectory(_keyStoreDir);
            }

            ILogManager logManager = NullLogManager.Instance;
            _serializer = new JsonSerializer(logManager);
            _cryptoRandom = new CryptoRandom();
            _store = new FileKeyStore(_configurationProvider, _serializer, new AesEncrypter(_configurationProvider, logManager), _cryptoRandom, logManager);

            var testsContent = File.ReadAllText("basic_tests.json");
            _testsModel = _serializer.Deserialize<KeyStoreTestsModel>(testsContent);

            _testAddress = Build.A.PrivateKey.TestObject.Address;
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
        public void EvilnonceTest()
        {
            var testModel = _testsModel.Evilnonce;
            RunTest(testModel);
        }

        private void RunTest(KeyStoreTestModel testModel)
        {
            //this is missed in json file
            testModel.Json.Crypto.Version = 1;

            //copy test key json to KeyStore
            var filePath = Path.Combine(_keyStoreDir, _testAddress.ToString());
            var keyFile = _serializer.Serialize(testModel.Json);
            File.WriteAllText(filePath, keyFile);

            //get key
            var securedPass = new SecureString();
            testModel.Password.ToCharArray().ToList().ForEach(x => securedPass.AppendChar(x));
            var key = _store.GetKey(_testAddress, securedPass);

            //verify private key
            Assert.AreEqual(ResultType.Success, key.Result.ResultType);
            Assert.AreEqual(new PrivateKey(Bytes.FromHexString(testModel.Priv)), key.PrivateKey);

            //clean up
            File.Delete(filePath);
        }

        private class KeyStoreTestsModel
        {
            public KeyStoreTestModel Test1 { get; set; }
            public KeyStoreTestModel Test2 { get; set; }
            public KeyStoreTestModel Python_generated_test_with_odd_iv { get; set; }
            public KeyStoreTestModel Evilnonce { get; set; }
        }

        private class KeyStoreTestModel
        {
            public KeyStoreItem Json { get; set; }
            public string Password { get; set; }
            public string Priv { get; set; }
        }
    } 
}