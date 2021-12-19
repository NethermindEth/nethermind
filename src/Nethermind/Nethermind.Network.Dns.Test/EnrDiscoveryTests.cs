using NUnit.Framework;

namespace Nethermind.Network.Dns.Test;

public class EnrDiscoveryTests
{
    [SetUp]
    public void Setup()
    {
    }

    [Test]
    public void Test_enr_discovery()
    {
        // do not run this test on CI
        // EnrDiscovery enrDiscovery = new();
        // Assert.AreEqual(3000, enrDiscovery.SearchTree("all.mainnet.ethdisco.net").Count);
    }
}
