// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test;

public class PeerPoolTests
{
    [Test]
    public async Task PeerPool_ShouldThrottleSource_WhenFull()
    {
        var trustedNodesManager = Substitute.For<ITrustedNodesManager>();

        TestNodeSource nodeSource = new TestNodeSource();
        PeerPool pool = new PeerPool(
            nodeSource,
            Substitute.For<INodeStatsManager>(),
            new NetworkStorage(new TestMemDb(), LimboLogs.Instance),
            new NetworkConfig()
            {
                MaxActivePeers = 5,
                MaxCandidatePeerCount = 10
            },
            LimboLogs.Instance,
            trustedNodesManager);

        Random rand = new Random(0);
        PrivateKeyGenerator keyGen = new PrivateKeyGenerator(new TestRandom((m) => rand.Next(m), (s) =>
        {
            byte[] buffer = new byte[s];
            rand.NextBytes(buffer);
            return buffer;
        }));

        for (int i = 0; i < 5; i++)
        {
            PublicKey key = keyGen.Generate().PublicKey;
            Node node = new Node(key, "1.2.3.4", 1234);
            Peer peer = pool.GetOrAdd(node);
            pool.ActivePeers[key] = peer;
        }

        pool.Start();

        for (int i = 0; i < 10; i++)
        {
            PublicKey key = keyGen.Generate().PublicKey;
            Node node = new Node(key, "1.2.3.4", 1234);
            nodeSource.AddNode(node);
        }

        Assert.That(() => nodeSource.BufferedNodeCount, Is.EqualTo(5).After(100, 10));

        await pool.StopAsync();
    }
}
