using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using DotNetty.Buffers;
using FluentAssertions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Enr;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Dns.Test;

public class EnrDiscoveryTests
{
    [Test]
    // [Explicit("Can take few minutes to run")]
    public async Task Test_enr_discovery()
    {
        int count = 0;
        NodeRecordSigner singer = new(new Ecdsa(), TestItem.PrivateKeyA);
        INodeRecordSigner countingSigner = Substitute.For<INodeRecordSigner>();
        countingSigner.Deserialize(Arg.Any<RlpStream>()).ReturnsForAnyArgs(c =>
        {
            NodeRecord result = singer.Deserialize(c.Arg<RlpStream>());
            count++;
            return result;
        });
        
        Stopwatch stopwatch = Stopwatch.StartNew();
        TestErrorLogManager testErrorLogManager = new();
        EnrDiscovery enrDiscovery = new(countingSigner, testErrorLogManager);
        
        int added = 0;
        enrDiscovery.NodeAdded += (o, e) => Interlocked.Increment(ref added); 
        await enrDiscovery.SearchTree("all.mainnet.ethdisco.net");
        await TestContext.Out.WriteLineAsync($"Actually added {added} in {stopwatch.Elapsed:g}");
        foreach (TestErrorLogManager.Error error in testErrorLogManager.Errors)
        {
            await TestContext.Out.WriteLineAsync(error.Text);
        }
        count.Should().Be(3000);
    }
}
