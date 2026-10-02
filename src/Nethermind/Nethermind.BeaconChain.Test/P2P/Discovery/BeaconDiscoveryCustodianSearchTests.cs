// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;
using IIPResolver = Nethermind.Network.IIPResolver;

namespace Nethermind.BeaconChain.Test.P2P.Discovery;

/// <summary>
/// fulu/das-core.md: a node must retrieve every column it samples, so once a sampled column has no connected custodian,
/// discovery offers the candidates custodying it first and re-offers its routing table without waiting for the periodic sweep.
/// </summary>
public class BeaconDiscoveryCustodianSearchTests
{
    private static readonly TimeSpan MinSweepInterval = TimeSpan.FromMilliseconds(500);

    // BeaconDiscovery.CandidateCapacity, the bound of the queue from the node sources to the custody ranking.
    private const int BeaconDiscoveryQueueCapacity = 256;

    /// <summary>With no custodian request, the sweep loop waits its interval and then sweeps; the production interval is within forty seconds.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task The_periodic_table_sweep_reoffers_nodes_within_forty_seconds(CancellationToken token)
    {
        Assert.That(BeaconDiscovery.TableSweepInterval, Is.LessThanOrEqualTo(TimeSpan.FromSeconds(40)));
        TimeSpan interval = TimeSpan.FromMilliseconds(300);
        await using BeaconDiscovery discovery = CreateDiscovery();
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        TaskCompletionSource swept = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Stopwatch waited = Stopwatch.StartNew();
        Task sweep = discovery.SweepTableAsync(() =>
        {
            swept.TrySetResult();
            return [];
        }, BeaconDiscovery.CreateNodeChannel().Writer, interval, TimeSpan.Zero, stop.Token);
        try
        {
            await swept.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
            Assert.That(waited.Elapsed, Is.GreaterThanOrEqualTo(interval - TimeSpan.FromMilliseconds(50)), "the sweep waits its interval first");
        }
        finally
        {
            await stop.CancelAsync();
            try
            {
                await sweep;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
        }
    }

    [Test]
    public async Task Discovery_orders_equal_custody_by_dial_history_then_fork_preparedness()
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        Channel<Node> nodes = BeaconDiscovery.CreateNodeChannel();
        Dictionary<PublicKey, BeaconPeerCandidate> candidates = [];
        foreach (string peerId in new[] { "plain", "known", "prepared" })
        {
            PublicKey key = TestItem.PrivateKeys[candidates.Count].PublicKey;
            candidates[key] = new(peerId, peerId, [], 1, "enr:") { ForkPreference = peerId == "prepared" ? 4 : 0 };
            nodes.Writer.TryWrite(new Node(key, "1.2.3.4", 9000));
        }

        discovery.DialHistory.Record("known", true);
        nodes.Writer.Complete();
        List<string> offered = [];
        await foreach (BeaconPeerCandidate candidate in discovery.OfferByCustodyAsync(nodes.Reader, node => candidates[node.Id], CancellationToken.None))
        {
            offered.Add(candidate.PeerId);
        }

        Assert.That(offered, Is.EqualTo(new[] { "known", "prepared", "plain" }));
    }

    [Test]
    public async Task A_continuously_refilled_node_queue_still_offers_a_candidate()
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        Channel<Node> nodes = BeaconDiscovery.CreateNodeChannel();
        Node node = new(TestItem.PublicKeyA, "1.2.3.4", 9000);
        nodes.Writer.TryWrite(node);
        int converted = 0;
        await foreach (BeaconPeerCandidate candidate in discovery.OfferByCustodyAsync(nodes.Reader, Convert, CancellationToken.None))
        {
            Assert.That(candidate.PeerId, Is.EqualTo("peer"));
            Assert.That(converted, Is.LessThanOrEqualTo(BeaconDiscoveryQueueCapacity));
            break;
        }

        BeaconPeerCandidate Convert(Node next)
        {
            if (++converted > BeaconDiscoveryQueueCapacity * 2) Assert.Fail("node conversion never yielded to dialing");
            nodes.Writer.TryWrite(next);
            return new("address", "peer", [], 1, "enr:");
        }
    }

    [Test]
    public async Task A_failed_ipv4_target_uses_ipv6_and_new_enr_sequences_do_not_clear_endpoint_backoff()
    {
        ManualTimestamper clock = new();
        await using BeaconDiscovery discovery = CreateDiscovery(clock);
        BeaconPeerCandidate candidate = new("ipv4", "peer", [], 1, "enr:") { Addresses = ["ipv4", "ipv6"] };
        discovery.DialHistory.Record("ipv4", false);
        Assert.That(discovery.SelectDialableAddress(candidate)!.Multiaddress, Is.EqualTo("ipv6"));
        discovery.DialHistory.Record("ipv6", false);
        Assert.That(discovery.SelectDialableAddress(candidate with { EnrSequence = 2 }), Is.Null);
        Assert.That(discovery.SelectDialableAddress(candidate with { Addresses = ["new-address"] })!.Multiaddress, Is.EqualTo("new-address"));
        clock.Add(PeerDialHistory.InitialBackoff * 1.2);
        Assert.That(discovery.SelectDialableAddress(candidate), Is.Not.Null);
        discovery.DialHistory.Record("ipv6", true);
        Assert.That(discovery.SelectDialableAddress(candidate)!.Multiaddress, Is.EqualTo("ipv6"));
    }

    [TestCase(new ulong[] { 1, 2 }, new[] { "supernode", "partial", "bystander" }, TestName = "Discovered candidates custodying more wanted columns are offered first")]
    [TestCase(new ulong[0], new[] { "bystander", "partial", "supernode" }, TestName = "Without a wanted column discovered candidates are offered in arrival order")]
    public async Task Discovered_candidates_are_offered_by_wanted_custody(ulong[] wanted, string[] expectedOrder)
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        discovery.RequestColumnCustodians(wanted);
        Dictionary<PublicKey, BeaconPeerCandidate> candidates = [];
        Channel<Node> nodes = Channel.CreateUnbounded<Node>();
        AddNode("bystander", [9]);
        AddNode("partial", [1]);
        AddNode("supernode", [.. Enumerable.Range(0, Eip7594DasConstants.NumberOfColumns).Select(static c => (ulong)c)]);
        nodes.Writer.Complete();

        List<string> offered = [];
        await foreach (BeaconPeerCandidate candidate in discovery.OfferByCustodyAsync(nodes.Reader, node => candidates[node.Id], CancellationToken.None))
        {
            offered.Add(candidate.PeerId);
        }

        Assert.That(offered, Is.EqualTo(expectedOrder));

        void AddNode(string peerId, ulong[] custodied)
        {
            PublicKey key = TestItem.PrivateKeys[candidates.Count].PublicKey;
            candidates[key] = new BeaconPeerCandidate($"/ip4/1.2.3.4/tcp/9000/p2p/{peerId}", peerId, [], 1, "enr:") { Custody = new PeerColumnCustody(custodied, isAdvertised: true) };
            nodes.Writer.TryWrite(new Node(key, "1.2.3.4", 9000));
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_table_sweep_larger_than_the_queue_does_not_drop_a_custodian_before_it_is_ranked(CancellationToken token)
    {
        const int tableSize = 300;
        await using BeaconDiscovery discovery = CreateDiscovery();
        Dictionary<PublicKey, BeaconPeerCandidate> candidates = [];
        List<Node> table = [];
        for (int i = 0; i < tableSize; i++)
        {
            byte[] keyBytes = new byte[32];
            keyBytes[30] = (byte)((i + 1) >> 8);
            keyBytes[31] = (byte)(i + 1);
            PublicKey key = new PrivateKey(keyBytes).PublicKey;
            // Iterated first, so a queue that drops its oldest entry loses it to the rest of the sweep.
            string peerId = i == 0 ? "custodian" : $"bystander-{i}";
            candidates[key] = new BeaconPeerCandidate($"/ip4/1.2.3.4/tcp/9000/p2p/{peerId}", peerId, [], 1, "enr:") { Custody = new PeerColumnCustody(i == 0 ? [1] : [9], isAdvertised: true) };
            table.Add(new Node(key, "1.2.3.4", 9000));
        }

        int iterated = 0;
        Channel<Node> nodes = BeaconDiscovery.CreateNodeChannel();
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task sweep = discovery.SweepTableAsync(Iterate, nodes.Writer, TimeSpan.FromHours(1), TimeSpan.FromHours(1), stop.Token);
        discovery.RequestColumnCustodians([1]);
        while (Volatile.Read(ref iterated) <= BeaconDiscoveryQueueCapacity)
        {
            await Task.Delay(10, token);
        }

        // A writer that drops instead of waiting finishes the whole sweep meanwhile.
        await Task.Delay(200, token);
        BeaconPeerCandidate? first = null;
        await foreach (BeaconPeerCandidate candidate in discovery.OfferByCustodyAsync(nodes.Reader, node => candidates[node.Id], stop.Token))
        {
            first = candidate;
            break;
        }

        await stop.CancelAsync();
        Assert.That(async () => await sweep, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(first?.PeerId, Is.EqualTo("custodian"), "the only custodian of a wanted column was dropped before custody ranking");

        IEnumerable<Node> Iterate()
        {
            foreach (Node node in table)
            {
                Interlocked.Increment(ref iterated);
                yield return node;
            }
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_newly_wanted_column_sweeps_the_routing_table_early_at_most_once_per_interval(CancellationToken token)
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        Stopwatch clock = Stopwatch.StartNew();
        List<TimeSpan> sweeps = [];
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task pump = discovery.SweepTableAsync(
            () =>
            {
                lock (sweeps)
                {
                    sweeps.Add(clock.Elapsed);
                }

                return [];
            },
            Channel.CreateUnbounded<Node>().Writer,
            TimeSpan.FromHours(1),
            MinSweepInterval,
            stop.Token);

        discovery.RequestColumnCustodians([1]);
        bool firstSweep = await SweepCountReaches(1, token);
        // Lands during the pause after the first early sweep.
        discovery.RequestColumnCustodians([1, 2]);
        bool secondSweep = await SweepCountReaches(2, token);
        await Task.Delay(MinSweepInterval * 1.5, token);
        discovery.RequestColumnCustodians([2, 1]);
        await Task.Delay(MinSweepInterval * 3, token);
        int finalCount = SweepCount();

        await stop.CancelAsync();
        Assert.That(async () => await pump, Throws.InstanceOf<OperationCanceledException>());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstSweep, Is.True, "a newly wanted column sweeps without waiting for the periodic interval");
            Assert.That(secondSweep, Is.True, "a column newly wanted during the pause after a sweep still sweeps");
            Assert.That(sweeps[1] - sweeps[0], Is.GreaterThanOrEqualTo(MinSweepInterval - TimeSpan.FromMilliseconds(20)), "early sweeps are spaced by the minimum interval");
            Assert.That(finalCount, Is.EqualTo(2), "a request naming no new column does not sweep");
        }

        int SweepCount()
        {
            lock (sweeps)
            {
                return sweeps.Count;
            }
        }

        async Task<bool> SweepCountReaches(int count, CancellationToken cancellation)
        {
            Stopwatch waited = Stopwatch.StartNew();
            while (SweepCount() < count && waited.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(10, cancellation);
            }

            return SweepCount() >= count;
        }
    }

    [Test]
    public async Task The_custodian_search_is_logged_at_most_once_a_minute_however_often_the_wanted_columns_change()
    {
        ManualTimestamper time = new(DateTime.UnixEpoch.AddYears(56));
        TestLogger logger = new();
        await using BeaconDiscovery discovery = CreateDiscovery(time, new OneLoggerLogManager(new ILogger(logger)));

        // Each request names a column the previous one did not, as a flapping custodian set would.
        for (int i = 0; i < 100; i++)
        {
            discovery.RequestColumnCustodians([(ulong)(i % 2) + 1]);
        }

        int linesWithinTheMinute = SearchLines();
        time.Add(TimeSpan.FromMinutes(1));
        discovery.RequestColumnCustodians([3]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(linesWithinTheMinute, Is.EqualTo(1));
            Assert.That(SearchLines(), Is.EqualTo(2), "a newly wanted column is logged again once the minute has passed");
        }

        int SearchLines() => logger.LogList.Count(static l => l.StartsWith("No connected beacon chain peer custodies", StringComparison.Ordinal));
    }

    private static BeaconDiscovery CreateDiscovery(ITimestamper? timestamper = null, ILogManager? logManager = null) =>
        new(new BeaconChainConfig { Discv5Port = 0 }, BeaconChainSpec.Mainnet, new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()),
            Substitute.For<IIPResolver>(), timestamper ?? Timestamper.Default, logManager ?? LimboLogs.Instance);
}
