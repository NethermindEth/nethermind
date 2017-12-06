using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nevermind.JsonRpc.Module;

namespace Nevermind.JsonRpc.Test
{
    [TestClass]
    public class NetModuleTests
    {
        private INetModule _netModule;

        [TestInitialize]
        public void Initialize()
        {
            _netModule = new NetModule();
        }

        [TestMethod]
        public void NetVersionSuccessTest()
        {
            var result = _netModule.net_version();
            Assert.AreEqual(result, "1");
        }
    }
}