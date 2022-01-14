using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Network.Dns.Test;

public class EnrDiscoveryTests
{
    [Test]
    [Explicit("Do not run this test on CI - takes a lot of time")]
    public async Task Test_enr_discovery()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        TestErrorLogManager testErrorLogManager = new();
        EnrDiscovery enrDiscovery = new(testErrorLogManager);
        int count = 0;
        enrDiscovery.NodeAdded += (o, e) => Interlocked.Increment(ref count); 
        await enrDiscovery.SearchTree("all.mainnet.ethdisco.net");
        await TestContext.Out.WriteLineAsync(stopwatch.Elapsed.ToString("g"));
        count.Should().Be(3000);
    }
}
