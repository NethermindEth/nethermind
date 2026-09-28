// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using IIPResolver = Nethermind.Network.IIPResolver;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.P2P.PeerBandTests;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>
/// fulu/das-core.md: a node must retrieve every column it samples, and fulu/p2p-interface.md peers serve only the columns
/// they custody, so the peer set must hold a custodian of each sampled column: one is sought through discovery when
/// missing, admitted past the target peer count, and never trimmed away as the last one.
/// </summary>
public class SampledColumnCustodianTests
{
    // Below 2^255: the pinned libp2p reads a secp256k1 private key as a signed integer.
    private const string PartialCustodianKey = "1c71a67e1177ad4e901695e1b4b9ee17ae16c6668d313eac2f96dbcda3f29111";
    private const string SupernodeKey = "2c71a67e1177ad4e901695e1b4b9ee17ae16c6668d313eac2f96dbcda3f29122";
    private const string LocalKey = "3c71a67e1177ad4e901695e1b4b9ee17ae16c6668d313eac2f96dbcda3f29133";

    [Test]
    [CancelAfter(60_000)]
    public async Task A_sampled_column_without_a_custodian_is_sought_through_discovery_and_only_its_custodian_is_admitted_past_the_target(CancellationToken token)
    {
        using PrivateKey partialKey = new(PartialCustodianKey);
        using PrivateKey supernodeKey = new(SupernodeKey);
        Node partial = CreateNode(partialKey, custodyGroupCount: Eip7594DasConstants.CustodyRequirement);
        Node bystander = CreateNode();
        Node supernode = CreateNode(supernodeKey, custodyGroupCount: Eip7594DasConstants.NumberOfCustodyGroups);
        Node client = CreateNode();
        SetMatchingStatus(partial, bystander, supernode, client);
        client.Config.TargetPeerCount = 1;
        await using BeaconDiscovery discovery = CreateDiscovery();

        await using (client.P2P)
        await using (partial.P2P)
        await using (bystander.P2P)
        await using (supernode.P2P)
        {
            await StartAsync(token, partial, bystander, supernode, client);
            PeerManager peerManager = new(client.P2P, client.Config, client.StatusHolder, LimboLogs.Instance, discovery);
            Assert.That(await peerManager.TryAddPeerAsync(LoopbackAddress(partial.P2P), token, Enr(partialKey, partial, Eip7594DasConstants.CustodyRequirement)), Is.True);

            PeerColumnCustody partialCustody = PeerColumnCustody.ForNode(partialKey.PublicKey.Hash, Eip7594DasConstants.CustodyRequirement);
            ulong[] uncustodied = [.. new DiscoveryNodeCustodySource(discovery).Current!.SampledColumns.Where(c => !partialCustody.Custodies(c))];
            IReadOnlyList<ulong> wantedWithOnePeer = [.. discovery.WantedColumns];
            Task admission = peerManager.WaitForAdmissionCapacityAsync(token);
            await Task.WhenAny(admission, Task.Delay(TimeSpan.FromSeconds(5), token));

            // The bystander is announced with the partial custodian's record, so it custodies no column still wanted.
            bool bystanderAdmitted = await peerManager.TryAddPeerAsync(LoopbackAddress(bystander.P2P), token, Enr(partialKey, bystander, Eip7594DasConstants.CustodyRequirement));
            bool supernodeAdmitted = await peerManager.TryAddPeerAsync(LoopbackAddress(supernode.P2P), token, Enr(supernodeKey, supernode, Eip7594DasConstants.NumberOfCustodyGroups));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(uncustodied, Is.Not.Empty, "a CUSTODY_REQUIREMENT peer custodies fewer columns than this node samples");
                Assert.That(wantedWithOnePeer, Is.EqualTo(uncustodied), "discovery is asked for exactly the sampled columns no connected peer custodies");
                Assert.That(admission.IsCompletedSuccessfully, Is.True, "at the target, a missing custodian still opens room for a dial");
                Assert.That(bystanderAdmitted, Is.False, "past the target, a candidate custodying no wanted column is not dialed");
                Assert.That(supernodeAdmitted, Is.True, "past the target, a candidate custodying a wanted column is dialed");
                Assert.That(peerManager.PeerCount, Is.EqualTo(2));
                Assert.That(discovery.WantedColumns, Is.Empty, "the supernode custodies every column");
            }
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Trimming_to_the_target_keeps_the_last_custodian_of_a_sampled_column([Values(1, 2)] int supernodeCount, CancellationToken token)
    {
        Node[] supernodes = [.. Enumerable.Range(0, supernodeCount).Select(static _ => CreateNode(custodyGroupCount: Eip7594DasConstants.NumberOfCustodyGroups))];
        Node partial = CreateNode(custodyGroupCount: Eip7594DasConstants.CustodyRequirement);
        Node client = CreateNode();
        SetMatchingStatus([.. supernodes, partial, client]);
        ulong supernodeHeadSlot = client.StatusHolder.CurrentStatus.HeadSlot;
        // The supernodes rank worst, so the trim would take them first.
        foreach (Node supernode in supernodes)
        {
            supernode.StatusHolder.CurrentStatus.HeadSlot = supernodeHeadSlot;
        }

        partial.StatusHolder.CurrentStatus.HeadSlot = supernodeHeadSlot + 100;
        await using BeaconDiscovery discovery = CreateDiscovery();

        try
        {
            await StartAsync(token, [.. supernodes, partial, client]);
            PeerManager peerManager = new(client.P2P, client.Config, client.StatusHolder, LimboLogs.Instance, discovery);
            foreach (Node node in (Node[])[.. supernodes, partial])
            {
                Assert.That(await peerManager.TryAddPeerAsync(LoopbackAddress(node.P2P), token), Is.True);
            }

            client.Config.MaxPeerCount = 1;
            client.Config.TargetPeerCount = 1;
            await peerManager.RunMaintenanceRoundAsync(token);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(peerManager.PeerCount, Is.EqualTo(1), "every peer whose sampled columns another kept peer also custodies is trimmed");
                Assert.That(peerManager.GetBestPeers(0).Single().HeadSlot, Is.EqualTo(supernodeHeadSlot),
                    "a CUSTODY_REQUIREMENT peer cannot custody all of this node's sampled columns, so one supernode is kept as their last custodian");
                Assert.That(discovery.WantedColumns, Is.Empty);
            }
        }
        finally
        {
            foreach (Node node in (Node[])[.. supernodes, partial, client])
            {
                await node.P2P.DisposeAsync();
            }
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_peer_dropped_for_repeated_failures_is_logged_at_info_with_its_last_failure(CancellationToken token)
    {
        SwitchableStatusSource serverStatus = new();
        Node server = PeerBandTests.CreateNode(serverStatus);
        Node client = CreateNode();
        SetMatchingStatus(server, client);
        serverStatus.Status = server.StatusHolder.CurrentStatus;
        TestLogger logger = new() { IsDebug = false, IsTrace = false };

        await using (client.P2P)
        await using (server.P2P)
        {
            await StartAsync(token, server, client);
            PeerManager peerManager = new(client.P2P, client.Config, client.StatusHolder, new OneLoggerLogManager(new ILogger(logger)));
            Assert.That(await peerManager.TryAddPeerAsync(LoopbackAddress(server.P2P), token), Is.True);
            ReportFailuresShortOfADrop(peerManager.GetBestPeers(0).Single());

            serverStatus.Refuse = true;
            for (int round = 0; round < 8 && peerManager.PeerCount > 0; round++)
            {
                await peerManager.RunMaintenanceRoundAsync(token);
            }

            string[] drops = [.. logger.LogList.Where(static l => l.StartsWith("Dropping beacon chain peer", StringComparison.Ordinal))];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(peerManager.PeerCount, Is.Zero);
                Assert.That(drops, Has.Length.EqualTo(1));
                Assert.That(drops.Single(), Does.Match(@"repeated failures, last: .+"), "the drop names the failure that caused it");
            }
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_last_custodian_failing_health_checks_is_dropped_and_its_columns_are_sought(CancellationToken token)
    {
        SwitchableStatusSource supernodeStatus = new();
        Node supernode = PeerBandTests.CreateNode(supernodeStatus);
        supernode.Metadata.Current.CustodyGroupCount = Eip7594DasConstants.NumberOfCustodyGroups;
        using PrivateKey partialKey = new(PartialCustodianKey);
        Node partial = CreateNode(partialKey, custodyGroupCount: Eip7594DasConstants.CustodyRequirement);
        Node client = CreateNode();
        SetMatchingStatus(supernode, partial, client);
        supernodeStatus.Status = supernode.StatusHolder.CurrentStatus;
        client.Config.TargetPeerCount = 2;
        client.Config.MaxPeerCount = 4;
        await using BeaconDiscovery discovery = CreateDiscovery();

        await using (client.P2P)
        await using (partial.P2P)
        await using (supernode.P2P)
        {
            await StartAsync(token, supernode, partial, client);
            PeerManager peerManager = new(client.P2P, client.Config, client.StatusHolder, LimboLogs.Instance, discovery);
            Assert.That(await peerManager.TryAddPeerAsync(LoopbackAddress(supernode.P2P), token), Is.True);
            Assert.That(await peerManager.TryAddPeerAsync(LoopbackAddress(partial.P2P), token), Is.True);
            IReadOnlyList<ulong> wantedWhileHealthy = [.. discovery.WantedColumns];
            Task admission = peerManager.WaitForAdmissionCapacityAsync(token);
            bool parkedWhileHealthy = !admission.IsCompleted;
            ReportFailuresShortOfADrop(peerManager.GetBestPeers(0).Single(p => p.Id == LoopbackAddress(supernode.P2P)));

            supernodeStatus.Refuse = true;
            for (int round = 0; round < 8 && peerManager.PeerCount > 1; round++)
            {
                await peerManager.RunMaintenanceRoundAsync(token);
            }

            // The admission poll interval is far longer than this bound, so only the shortfall can wake the wait in time.
            bool wokenByShortfall = await Task.WhenAny(admission, Task.Delay(TimeSpan.FromSeconds(5), token)) == admission;
            PeerColumnCustody partialCustody = PeerColumnCustody.ForNode(partialKey.PublicKey.Hash, Eip7594DasConstants.CustodyRequirement);
            ulong[] onlySupernodeCustodied = [.. new DiscoveryNodeCustodySource(discovery).Current!.SampledColumns.Where(c => !partialCustody.Custodies(c))];

            using (Assert.EnterMultipleScope())
            {
                Assert.That(wantedWhileHealthy, Is.Empty, "the supernode custodies every sampled column");
                Assert.That(parkedWhileHealthy, Is.True, "at the target with every sampled column custodied, no room is opened");
                Assert.That(peerManager.PeerCount, Is.EqualTo(1), "a peer failing its health checks cannot serve columns, so being the last custodian does not keep it");
                Assert.That(onlySupernodeCustodied, Is.Not.Empty);
                Assert.That(discovery.WantedColumns, Is.EqualTo(onlySupernodeCustodied), "the columns only the dropped peer custodied are sought through discovery");
                Assert.That(wokenByShortfall, Is.True, "the parked admission wait wakes once a sampled column loses its custodian");
            }
        }
    }

    /// <summary>
    /// A last custodian whose requests keep failing is kept connected, since it is still the only source of its columns
    /// (fulu/das-core.md), but it is not picked for requests and its columns count as lacking a custodian, so a replacement is sought and admitted.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public async Task A_last_custodian_at_the_failure_limit_stays_connected_but_is_not_picked_and_a_replacement_is_admitted(CancellationToken token)
    {
        Node failing = CreateNode(custodyGroupCount: Eip7594DasConstants.NumberOfCustodyGroups);
        using PrivateKey partialKey = new(PartialCustodianKey);
        Node partial = CreateNode(partialKey, custodyGroupCount: Eip7594DasConstants.CustodyRequirement);
        using PrivateKey replacementKey = new(SupernodeKey);
        Node replacement = CreateNode(replacementKey, custodyGroupCount: Eip7594DasConstants.NumberOfCustodyGroups);
        Node client = CreateNode();
        SetMatchingStatus(failing, partial, replacement, client);
        client.Config.TargetPeerCount = 2;
        client.Config.MaxPeerCount = 4;
        await using BeaconDiscovery discovery = CreateDiscovery();

        await using (client.P2P)
        await using (failing.P2P)
        await using (partial.P2P)
        await using (replacement.P2P)
        {
            await StartAsync(token, failing, partial, replacement, client);
            PeerManager peerManager = new(client.P2P, client.Config, client.StatusHolder, LimboLogs.Instance, discovery);
            Assert.That(await peerManager.TryAddPeerAsync(LoopbackAddress(failing.P2P), token), Is.True);
            Assert.That(await peerManager.TryAddPeerAsync(LoopbackAddress(partial.P2P), token), Is.True);
            IReadOnlyList<ulong> uncustodiedWhileHealthy = peerManager.UncustodiedSampledColumns();
            IBeaconSyncPeer failingPeer = peerManager.GetBestPeers(0).Single(p => p.Id == LoopbackAddress(failing.P2P));
            for (int i = 0; i < 8; i++)
            {
                failingPeer.ReportFailure(PeerFailureReason.RequestFailed);
            }

            PeerColumnCustody partialCustody = PeerColumnCustody.ForNode(partialKey.PublicKey.Hash, Eip7594DasConstants.CustodyRequirement);
            ulong[] onlyFailingCustodied = [.. new DiscoveryNodeCustodySource(discovery).Current!.SampledColumns.Where(c => !partialCustody.Custodies(c))];
            string[] picked = [.. peerManager.GetBestPeers(0).Select(static p => p.Id)];
            IReadOnlyList<ulong> uncustodied = peerManager.UncustodiedSampledColumns();
            int connected = peerManager.PeerCount;
            bool replacementAdmitted = await peerManager.TryAddPeerAsync(LoopbackAddress(replacement.P2P), token, Enr(replacementKey, replacement, Eip7594DasConstants.NumberOfCustodyGroups));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(uncustodiedWhileHealthy, Is.Empty);
                Assert.That(onlyFailingCustodied, Is.Not.Empty);
                Assert.That(picked, Is.EqualTo(new[] { LoopbackAddress(partial.P2P) }), "a peer at the failure limit is not picked for requests");
                Assert.That(connected, Is.EqualTo(2), "the last custodian of a sampled column stays connected");
                Assert.That(uncustodied, Is.EqualTo(onlyFailingCustodied), "the columns only the failing peer custodies are sought");
                Assert.That(replacementAdmitted, Is.True, "at the target, a custodian of those columns is admitted");
            }
        }
    }

    /// <summary>
    /// At <see cref="IBeaconChainConfig.MaxPeerCount"/> nothing trims the pool, so a candidate custodying a sampled column no connected
    /// peer custodies is dialed past the ceiling and, once admitted, takes the place of the worst peer; it is not dialed while that
    /// peer would stay the last custodian of a sampled column, and a dial that fails costs no connected peer.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public async Task At_the_peer_ceiling_a_candidate_custodying_a_missing_column_replaces_the_worst_peer_unless_it_is_a_last_custodian(
        [Values] CeilingCase ceilingCase, CancellationToken token)
    {
        bool connectedIsLastCustodian = ceilingCase == CeilingCase.ConnectedIsLastCustodian;
        using PrivateKey localKey = new(LocalKey);
        await using BeaconDiscovery discovery = CreateDiscovery(localKey);
        ulong[] sampled = [.. new DiscoveryNodeCustodySource(discovery).Current!.SampledColumns];
        using PrivateKey connectedKey = FindPartialCustodian(custody => custody.CountCustodied(sampled) > 0 == connectedIsLastCustodian);
        PeerColumnCustody connectedCustody = PeerColumnCustody.ForNode(connectedKey.PublicKey.Hash, Eip7594DasConstants.CustodyRequirement);
        // Custodies a sampled column the connected peer does not, and, when the connected peer custodies any, misses one of those.
        using PrivateKey candidateKey = FindPartialCustodian(custody =>
            sampled.Any(c => custody.Custodies(c) && !connectedCustody.Custodies(c))
            && sampled.Any(c => connectedCustody.Custodies(c) && !custody.Custodies(c)) == connectedIsLastCustodian);
        Node connected = CreateNode(connectedKey, custodyGroupCount: Eip7594DasConstants.CustodyRequirement);
        Node candidate = CreateNode(candidateKey, custodyGroupCount: Eip7594DasConstants.CustodyRequirement);
        Node client = CreateNode();
        SetMatchingStatus(connected, candidate, client);
        client.Config.TargetPeerCount = 1;
        client.Config.MaxPeerCount = 1;

        await using (client.P2P)
        await using (connected.P2P)
        await using (candidate.P2P)
        {
            await StartAsync(token, connected, candidate, client);
            string candidateAddress = LoopbackAddress(candidate.P2P);
            string candidateEnr = Enr(candidateKey, candidate, Eip7594DasConstants.CustodyRequirement);
            if (ceilingCase == CeilingCase.CandidateUnreachable)
            {
                // The candidate's identity at a port nothing listens on.
                candidateAddress = Regex.Replace(candidateAddress, "/tcp/[0-9]+/", "/tcp/1/");
            }

            PeerManager peerManager = new(client.P2P, client.Config, client.StatusHolder, LimboLogs.Instance, discovery);
            Assert.That(await peerManager.TryAddPeerAsync(LoopbackAddress(connected.P2P), token), Is.True);

            Task admission = peerManager.WaitForAdmissionCapacityAsync(token);
            bool admissionOpened = await Task.WhenAny(admission, Task.Delay(TimeSpan.FromSeconds(5), token)) == admission;
            bool candidateAdmitted = await peerManager.TryAddPeerAsync(candidateAddress, token, candidateEnr);
            bool replaces = ceilingCase == CeilingCase.ConnectedCustodiesNoSampledColumn;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(admissionOpened, Is.True, "at the ceiling, a sampled column without a custodian still opens room for a dial");
                Assert.That(candidateAdmitted, Is.EqualTo(replaces));
                Assert.That(peerManager.GetBestPeers(0).Select(static p => p.Id), Is.EqualTo(new[] { replaces ? candidateAddress : LoopbackAddress(connected.P2P) }),
                    ceilingCase switch
                    {
                        CeilingCase.ConnectedIsLastCustodian => "the last custodian of a sampled column is not dropped to make room",
                        CeilingCase.CandidateUnreachable => "a candidate that cannot be reached takes no connected peer's place",
                        _ => "the peer custodying no sampled column made room for the candidate",
                    });
            }
        }
    }

    /// <summary>
    /// Only a dial admitted past the ceiling to replace a peer drops one on landing. A dial reserved below the ceiling that lands after
    /// the pool reached the ceiling meanwhile, as when another admission took the last place, drops none and leaves the excess to the trim.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public async Task A_dial_reserved_below_the_ceiling_drops_no_peer_when_the_pool_reaches_the_ceiling_meanwhile(CancellationToken token)
    {
        SwitchableStatusSource lateStatus = new();
        Node connected = CreateNode();
        Node late = PeerBandTests.CreateNode(lateStatus);
        Node client = CreateNode();
        SetMatchingStatus(connected, late, client);
        lateStatus.Status = late.StatusHolder.CurrentStatus;
        client.Config.TargetPeerCount = 2;
        client.Config.MaxPeerCount = 2;

        await using (client.P2P)
        await using (connected.P2P)
        await using (late.P2P)
        {
            await StartAsync(token, connected, late, client);
            PeerManager peerManager = new(client.P2P, client.Config, client.StatusHolder, LimboLogs.Instance);
            Assert.That(await peerManager.TryAddPeerAsync(LoopbackAddress(connected.P2P), token), Is.True);

            // The late peer answers the admission's status request after the reservation, so the ceiling drops under the dial in flight.
            lateStatus.OnNextRead = () => client.Config.MaxPeerCount = 1;
            bool lateAdmitted = await peerManager.TryAddPeerAsync(LoopbackAddress(late.P2P), token);
            int afterLanding = peerManager.PeerCount;
            await peerManager.RunMaintenanceRoundAsync(token);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(lateAdmitted, Is.True);
                Assert.That(afterLanding, Is.EqualTo(2), "a dial that replaces no peer drops none");
                Assert.That(peerManager.PeerCount, Is.EqualTo(1), "the trim brings the pool back to the ceiling");
            }
        }
    }

    /// <summary>Records health-check-sized failures on <paramref name="peer"/> until one more failed health check drops it.</summary>
    private static void ReportFailuresShortOfADrop(IBeaconSyncPeer peer)
    {
        // One below PeerManager's consecutive failure limit of 8, so the test needs one maintenance round, not eight.
        for (int i = 0; i < 7; i++)
        {
            peer.ReportFailure(PeerFailureReason.RequestFailed);
        }
    }

    public enum CeilingCase
    {
        ConnectedIsLastCustodian,
        ConnectedCustodiesNoSampledColumn,
        CandidateUnreachable,
    }

    /// <summary>A <c>CUSTODY_REQUIREMENT</c> identity whose custody satisfies <paramref name="wanted"/>; deterministic, since the keys are.</summary>
    private static PrivateKey FindPartialCustodian(Func<PeerColumnCustody, bool> wanted)
    {
        for (int i = 0; i < 4096; i++)
        {
            PrivateKey key = new($"4c71a67e1177ad4e901695e1b4b9ee17ae16c6668d313eac2f96dbcda3f2{i:x4}");
            if (wanted(PeerColumnCustody.ForNode(key.PublicKey.Hash, Eip7594DasConstants.CustodyRequirement)))
            {
                return key;
            }

            key.Dispose();
        }

        throw new InvalidOperationException("No key in the searched range has the wanted custody");
    }

    private static Node CreateNode(PrivateKey? identity = null, ulong custodyGroupCount = Eip7594DasConstants.CustodyRequirement)
    {
        Node node = PeerBandTests.CreateNode();
        node.Metadata.Current.CustodyGroupCount = custodyGroupCount;
        if (identity is not null)
        {
            node.Store.PutMetadata(BeaconDiscovery.IdentityMetadataKey, identity.KeyBytes);
        }

        return node;
    }

    private static async Task StartAsync(CancellationToken token, params Node[] nodes)
    {
        foreach (Node node in nodes)
        {
            await node.P2P.StartAsync(token);
        }
    }

    private static string Enr(PrivateKey key, Node node, ulong custodyGroupCount)
    {
        int port = int.Parse(LoopbackAddress(node.P2P).Split('/')[4]);
        return new BeaconNodeRecordProvider(key, IPAddress.Loopback, port, port, EnrForkId.Compute(BeaconChainSpec.Mainnet, 0), custodyGroupCount).Current.ToString();
    }

    /// <summary>This node's identity and sampled columns, resolved as discovery's start does without binding a socket.</summary>
    /// <param name="identity">Pins the identity, and so the sampled columns; random when omitted.</param>
    private static BeaconDiscovery CreateDiscovery(PrivateKey? identity = null)
    {
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        if (identity is not null)
        {
            store.PutMetadata(BeaconDiscovery.IdentityMetadataKey, identity.KeyBytes);
        }

        BeaconDiscovery discovery = new(new BeaconChainConfig { Discv5Port = 0 }, BeaconChainSpec.Mainnet, store,
            Substitute.For<IIPResolver>(), Timestamper.Default, LimboLogs.Instance);
        discovery.CreateDiscv5Services(IPAddress.Loopback);
        return discovery;
    }

    /// <summary>Answers <c>status</c> until <see cref="Refuse"/> is set, then fails every request.</summary>
    private sealed class SwitchableStatusSource : IBeaconChainStatusSource
    {
        public volatile bool Refuse;

        /// <summary>Runs once, on the next read of <see cref="CurrentStatus"/>.</summary>
        public Action? OnNextRead;

        public StatusMessageV2 Status { get; set; } = null!;

        public StatusMessageV2 CurrentStatus
        {
            get
            {
                Interlocked.Exchange(ref OnNextRead, null)?.Invoke();
                return Refuse ? throw new Eth2ReqRespException("status refused") : Status;
            }
        }

        public Hash256 JustifiedRoot => Hash256.Zero;

        public bool ExecutionInSync => false;
    }
}
