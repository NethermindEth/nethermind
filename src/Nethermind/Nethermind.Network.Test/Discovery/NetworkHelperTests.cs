using Nethermind.Core;
using Nethermind.Core.Logging;
using NUnit.Framework;

namespace Nethermind.Network.Test.Discovery
{
    [TestFixture]
    public class NetworkHelperTests
    {
        [Test]
        public void ExternalIpTest()
        {
            var networkHelper = new NetworkHelper(NullLogger.Instance);
            var address = networkHelper.GetExternalIp();
            Assert.IsNotNull(address);
        }

        [Test]
        public void InternalIpTest()
        {
            var networkHelper = new NetworkHelper(NullLogger.Instance);
            var address = networkHelper.GetLocalIp();
            Assert.IsNotNull(address);
        }
    }
}