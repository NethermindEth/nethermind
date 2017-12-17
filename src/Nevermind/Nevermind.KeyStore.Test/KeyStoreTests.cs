using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nevermind.Core;
using Nevermind.Json;
using Nevermind.Utils.Model;

namespace Nevermind.KeyStore.Test
{
    [TestClass]
    public class KeyStoreTests
    {
        [TestMethod]
        public void GenerateKeyTest()
        {
            var configurationProvider = new ConfigurationProvider();
            var logger = new ConsoleLogger();
            IKeyStore store = new FileKeyStore(configurationProvider, new JsonSerializer(logger), new AesEncrypter(configurationProvider, logger), logger);
            var testPass = "testpassword";
            var key = store.GenerateKey(testPass);
            Assert.AreEqual(key.Item2.ResultType, ResultType.Success);

            var persistedKey = store.GetKey(key.Item1.Address, testPass);
            Assert.AreEqual(persistedKey.Item2.ResultType, ResultType.Success);

            Assert.IsTrue(key.Item1.Equals(persistedKey.Item1));

            var result = store.DeleteKey(key.Item1.Address, testPass);
            Assert.AreEqual(result.ResultType, ResultType.Success);

            var deletedKey = store.GetKey(key.Item1.Address, testPass);
            Assert.AreEqual(deletedKey.Item2.ResultType, ResultType.Failure);
        }
    }
}
