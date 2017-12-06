using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nevermind.JsonRpc.Module;

namespace Nevermind.JsonRpc.Test
{
    [TestClass]
    public class EthModuleTests
    {
        private IEthModule _ethModule;

        [TestInitialize]
        public void Initialize()
        {
            _ethModule = new EthModule();
        }

        [TestMethod]
        public void GetBalanceSuccessTest()
        {
            
        }
    }
}