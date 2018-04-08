using Nethermind.Core;
using Nethermind.Network;
using NUnit.Framework;

namespace Nethermind.Discovery.Test
{
    [TestFixture]
    public class NetworkHelperTests
    {
        [Test]
        public void ExternalIpTest()
        {
            var networkHelper = new NetworkHelper(new ConsoleLogger());
            var address = networkHelper.GetExternalIp();
            Assert.IsNotNull(address);
        }

        [Test]
        public void InternalIpTest()
        {
            var networkHelper = new NetworkHelper(new ConsoleLogger());
            var address = networkHelper.GetLocalIp();
            Assert.IsNotNull(address);
        }
    }
}