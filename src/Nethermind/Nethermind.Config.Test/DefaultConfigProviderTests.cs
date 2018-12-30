using Nethermind.JsonRpc;
using Nethermind.KeyStore.Config;
using Nethermind.Network.Config;
using Nethermind.Stats;
using NUnit.Framework;

namespace Nethermind.Config.Test
{
    [TestFixture]
    public class DefaultConfigProviderTests
    {
        private IConfigProvider _configProvider;

        [SetUp]
        public void Initialize()
        {
            var keystoreConfig = new KeystoreConfig();
            var networkConfig = new NetworkConfig();
            var jsonRpcConfig = new JsonRpcConfig();
            var statsConfig = new StatsConfig();
            _configProvider = new ConfigProvider();
        }
        
        [Test]
        public void Test()
        {
            var config = _configProvider.GetConfig<IStatsConfig>();
            Assert.IsInstanceOf<StatsConfig>(config);
        }
    }
}