// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Dns.Test;

[Parallelizable(ParallelScope.All)]
[Explicit("CI blocks DNS requests.")]
[TestFixture]
public class EnrDiscoveryTests
{
    [TestCase("all.mainnet.ethdisco.net")]
    [TestCase("enrtree://AKA3AM6LPBYEUDMVNU3BSVQJ5AD45Y7YPOHJLEF6W26QOE4VTUDPE@all.mainnet.ethdisco.net")]
    public async Task Test_enr_discovery(string url)
    {
        NodeRecordSigner singer = new(new Ecdsa(), TestItem.PrivateKeyA);
        TestErrorLogManager testErrorLogManager = new();
        INetworkConfig config = new NetworkConfig();
        config.DiscoveryDns = url;
        EnrDiscovery enrDiscovery = new(new EnrRecordParser(singer), config, testErrorLogManager);
        long startTime = Stopwatch.GetTimestamp();
        List<Node> addedRecords = enrDiscovery.DiscoverNodes(default).ToBlockingEnumerable().ToList();

        await TestContext.Out.WriteLineAsync($"Actually added {addedRecords.Count} in {Stopwatch.GetElapsedTime(startTime):g}");
        foreach (TestErrorLogManager.Error error in testErrorLogManager.Errors)
        {
            await TestContext.Out.WriteLineAsync(error.Text);
        }
        addedRecords.Count.Should().Be(3000);
    }

    [Test]
    public async Task Test_enr_discovery2()
    {

        NodeRecordSigner singer = new(new Ecdsa(), TestItem.PrivateKeyA);
        EnrRecordParser parser = new(singer);
        EnrTreeCrawler crawler = new(new(Substitute.For<InterfaceLogger>()));
        int verified = 0;
        await foreach (string record in crawler.SearchTree("all.mainnet.ethdisco.net"))
        {
            NodeRecord nodeRecord = parser.ParseRecord(record);
            if (!nodeRecord.Snap)
            {
                nodeRecord.EnrString.Should().BeEquivalentTo(record);
                verified++;
            }
        }

        await TestContext.Out.WriteLineAsync($"Verified {verified}");
    }
}
