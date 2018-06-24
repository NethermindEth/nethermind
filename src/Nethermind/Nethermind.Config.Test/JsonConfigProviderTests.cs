using System;
using System.IO;
using System.Linq;
using Castle.Core.Logging;
using NUnit.Framework;

namespace Nethermind.Config.Test
{
    [TestFixture]
    public class JsonConfigProviderTests
    {
        private JsonConfigProvider _configProvider;

        [SetUp]
        public void Initialize()
        {
            _configProvider = new JsonConfigProvider();
        }

        [Test]
        public void TestLoadJsonConfig()
        {
            _configProvider.LoadJsonConfig("SampleJsonConfig.json");

            Assert.AreEqual(100, _configProvider.KeystoreConfig.KdfparamsDklen);
            Assert.AreEqual("test", _configProvider.KeystoreConfig.Cipher);

            Assert.AreEqual("test", _configProvider.JsonRpcConfig.JsonRpcVersion);           
            Assert.AreEqual("UTF7", _configProvider.JsonRpcConfig.MessageEncoding);
            Assert.AreEqual(2, _configProvider.JsonRpcConfig.EnabledModules.Count());
            new[] { ConfigJsonRpcModuleType.Eth, ConfigJsonRpcModuleType.Shh }.ToList().ForEach(x =>
            {
                Assert.IsTrue(_configProvider.JsonRpcConfig.EnabledModules.Contains(x));
            });

            Assert.AreEqual(4, _configProvider.NetworkConfig.Concurrency);
            Assert.AreEqual(3, _configProvider.NetworkConfig.PenalizedReputationLocalDisconnectReasons.Length);
            new[] { ConfigDisconnectReason.UnexpectedIdentity, ConfigDisconnectReason.IncompatibleP2PVersion, ConfigDisconnectReason.BreachOfProtocol }
                .ToList().ForEach(x =>
            {
                Assert.IsTrue(_configProvider.NetworkConfig.PenalizedReputationLocalDisconnectReasons.Contains(x));
            });
            Assert.AreEqual(2, _configProvider.NetworkConfig.BootNodes.Length);

            var node1 = _configProvider.NetworkConfig.BootNodes.FirstOrDefault(x => x.NodeId == "testNodeId");
            Assert.IsNotNull(node1);
            Assert.AreEqual("testHist", node1.Host);
            Assert.AreEqual(43, node1.Port);

            var node2 = _configProvider.NetworkConfig.BootNodes.FirstOrDefault(x => x.NodeId == "testNodeId2");
            Assert.IsNotNull(node2);
            Assert.AreEqual("testHist2", node2.Host);
            Assert.AreEqual(44, node2.Port);
        }
    }
}
