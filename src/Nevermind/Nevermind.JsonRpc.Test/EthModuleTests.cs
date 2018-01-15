using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nevermind.Blockchain;
using Nevermind.Core;
using Nevermind.Core.Extensions;
using Nevermind.Json;
using Nevermind.JsonRpc.Module;
using Nevermind.Store;

namespace Nevermind.JsonRpc.Test
{
    [TestClass]
    public class EthModuleTests
    {
        private IEthModule _ethModule;

        [TestInitialize]
        public void Initialize()
        {
            var logger = new ConsoleLogger();
            //_ethModule = new EthModule(logger, new JsonSerializer(logger), new BlockchainProcessor(), new StateProvider() );
        }

        [TestMethod]
        public void GetBalanceSuccessTest()
        {
            var hex = new Hex(1024.ToBigEndianByteArray()).ToString(true, true);
        }
    }
}