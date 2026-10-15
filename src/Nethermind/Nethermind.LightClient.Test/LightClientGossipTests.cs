// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using Google.Protobuf;
using Multiformats.Address.Net;
using Nethermind.BeaconChain;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.LightClient.Consensus;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using Nethermind.Logging;
using Snappier;

namespace Nethermind.LightClient.Test;

public class LightClientGossipTests
{
    [Test]
    [CancelAfter(120_000)]
    public async Task Signed_finality_gossip_crosses_a_real_libp2p_connection_and_authenticates([Values] bool quic, CancellationToken token)
    {
        if (quic && OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            Assert.That(QuicListener.IsSupported && QuicConnection.IsSupported, Is.True, "Windows CI must exercise QUIC with the bundled MsQuic runtime.");
        if (quic && (!QuicListener.IsSupported || !QuicConnection.IsSupported)) Assert.Ignore("MsQuic is unavailable on this runner.");
        ManualClock clock = new();
        clock.SetSlot(10, 6);
        LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(1, 1);
        LightClientStore store = new(ConsensusTests.Spec, SszRoots.HashTreeRoot(bootstrap.Header!.Beacon!), bootstrap, 10);
        await using LightClientGossip gossip = new(ConsensusTests.Spec, LimboLogs.Instance, clock);
        int senderQuicPort = quic ? FreeUdpPort() : 0;
        await using BeaconP2P sender = CreateHost(quicPort: senderQuicPort);
        await using BeaconP2P receiver = CreateHost(gossip, quic ? FreeUdpPort() : 0);
        await sender.StartAsync(token);
        await receiver.StartAsync(token);
        TaskCompletionSource received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        gossip.Start(receiver.GetTopic, (update, _) =>
        {
            ulong before = store.FinalizedHeader.Beacon!.Slot;
            store.Process(update, ConsensusTests.Spec.GetSlotAtTime((ulong)clock.GetUtcNow().ToUnixTimeSeconds()));
            received.TrySetResult();
            return Task.FromResult(store.FinalizedHeader.Beacon!.Slot > before);
        }, static (_, _) => Task.FromResult(false), token);
        PeerManager manager = new(sender, new BeaconChainConfig { P2PPort = 0, QuicPort = senderQuicPort },
            new BeaconChainStatusHolder(ConsensusTests.Spec, Timestamper.Default), LimboLogs.Instance);
        int port = receiver.ListenAddresses.Single(address => address.ToString().Contains(quic ? "/quic-v1" : "/tcp/", StringComparison.Ordinal)).ToEndPoint().Port;
        string transport = quic ? $"udp/{port}/quic-v1" : $"tcp/{port}";
        Assert.That(await manager.TryAddPeerAsync($"/ip4/127.0.0.1/{transport}/p2p/{receiver.LocalPeerId}", token), Is.True);

        for (ulong slot = 4; !received.Task.IsCompleted; slot++)
        {
            clock.SetSlot(slot + 1, 6);
            gossip.RefreshTopics(receiver.GetTopic);
            LightClientFinalityUpdate update = Finality(ConsensusTests.Update(2, slot - 1, slot, 1));
            string topic = GossipTopics.Topic(ForkDigest.Compute(ConsensusTests.Spec, ConsensusTests.Spec.GetEpoch(slot - 1)),
                GossipTopics.LightClientFinalityUpdate);
            sender.GetTopic(topic).Publish(Snappy.CompressToArray(LightClientWireCodec.EncodeFinality(update, ConsensusTests.Spec)));
            await Task.WhenAny(received.Task, Task.Delay(500, token));
            token.ThrowIfCancellationRequested();
        }
        Assert.That(store.FinalizedHeader.Beacon!.Slot, Is.EqualTo(2));
    }

    private static BeaconP2P CreateHost(LightClientGossip? gossip = null, int quicPort = 0) => new(new BeaconChainConfig { P2PPort = 0, QuicPort = quicPort },
        ConsensusTests.Spec, new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>(), ConsensusTests.Spec),
        new BeaconChainStatusHolder(ConsensusTests.Spec, Timestamper.Default), new LocalMetadataSource(),
        new DataColumnSidecarPool(), new ExecutionPayloadEnvelopePool(), LimboLogs.Instance,
        configureGossip: gossip is null ? null : gossip.Configure);

    private static int FreeUdpPort()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    [Test]
    public async Task Finality_then_matching_optimistic_are_authenticated_and_forwarded_once()
    {
        await using Fixture fixture = new();
        LightClientUpdate update = ConsensusTests.Update(2, 3, 4, 1);
        LightClientFinalityUpdate finality = Finality(update);
        LightClientOptimisticUpdate optimistic = Optimistic(update);

        MessageValidity first = await fixture.Handle(finality);
        MessageValidity second = await fixture.Handle(optimistic);
        MessageValidity duplicate = await fixture.Handle(optimistic);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(first, Is.EqualTo(MessageValidity.Accepted));
        Assert.That(second, Is.EqualTo(MessageValidity.Accepted));
        Assert.That(duplicate, Is.EqualTo(MessageValidity.Ignored));
        Assert.That(fixture.Store.FinalizedHeader.Beacon!.Slot, Is.EqualTo(2));
        Assert.That(fixture.Store.OptimisticHeader.Beacon!.Slot, Is.EqualTo(3));
    }

    [Test]
    public async Task Invalid_signature_is_rejected_without_advancing_the_store()
    {
        await using Fixture fixture = new();
        LightClientOptimisticUpdate update = Optimistic(ConsensusTests.Update(2, 3, 4, 1));
        update.SyncAggregate!.SyncCommitteeSignature = new BlsSignature(new byte[96]);

        Assert.That(await fixture.Handle(update), Is.EqualTo(MessageValidity.Rejected));
        Assert.That(fixture.Store.OptimisticHeader.Beacon!.Slot, Is.EqualTo(1));
    }

    [Test]
    public async Task Early_message_is_ignored_before_signature_rejection([Values] bool valid)
    {
        await using Fixture fixture = new();
        LightClientOptimisticUpdate update = Optimistic(ConsensusTests.Update(2, 3, 4, 1));
        fixture.Clock.SetSlot(4, 0);
        if (!valid) update.SyncAggregate!.SyncCommitteeSignature = new BlsSignature(new byte[96]);

        Assert.That(await fixture.Handle(update), Is.EqualTo(MessageValidity.Ignored));
        Assert.That(fixture.Store.OptimisticHeader.Beacon!.Slot, Is.EqualTo(valid ? 3 : 1));
    }

    [Test]
    public async Task Wrong_fork_digest_is_rejected_without_processing()
    {
        await using Fixture fixture = new();
        LightClientOptimisticUpdate update = Optimistic(ConsensusTests.Update(2, 3, 4, 1));
        string wrong = GossipTopics.Topic(ForkDigest.Compute(ConsensusTests.Spec, 1), GossipTopics.LightClientOptimisticUpdate);

        Assert.That(await fixture.Gossip.HandleAsync(wrong,
            Snappy.CompressToArray(LightClientWireCodec.EncodeOptimistic(update, ConsensusTests.Spec)), static _ => true, CancellationToken.None),
            Is.EqualTo(MessageValidity.Rejected));
        Assert.That(fixture.Store.OptimisticHeader.Beacon!.Slot, Is.EqualTo(1));
    }

    [Test]
    public async Task Malformed_and_oversized_payloads_are_rejected([Values] bool oversized)
    {
        await using Fixture fixture = new();
        byte[] payload = oversized ? new byte[LightClientGossip.MaxUpdateBytes + 1] : [0, 1, 2];
        Assert.That(await fixture.Gossip.HandleAsync(fixture.Topic(GossipTopics.LightClientOptimisticUpdate),
            Snappy.CompressToArray(payload), static _ => true, CancellationToken.None), Is.EqualTo(MessageValidity.Rejected));
        Assert.That(fixture.Store.OptimisticHeader.Beacon!.Slot, Is.EqualTo(1));
    }

    [Test]
    public async Task Topics_rotate_before_fork_and_retire_two_epochs_afterwards()
    {
        await using Fixture fixture = new();
        string old = fixture.Topic(GossipTopics.LightClientOptimisticUpdate);
        string next = GossipTopics.Topic(ForkDigest.Compute(ConsensusTests.Spec, 1), GossipTopics.LightClientOptimisticUpdate);
        Assert.That(fixture.Topics.ContainsKey(next), Is.True);
        fixture.Clock.SetSlot(96, 6);
        fixture.Gossip.RefreshTopics(fixture.GetTopic);

        Assert.That(fixture.Topics[old].UnsubscribeCount, Is.EqualTo(1));
        Assert.That(fixture.Gossip.Verify(null!, new Message { Topic = old, Data = ByteString.Empty }), Is.EqualTo(MessageValidity.Throttled));
        Assert.That(fixture.Gossip.Verify(null!, new Message { Topic = next, Data = ByteString.Empty }), Is.EqualTo(MessageValidity.Deferred));
    }

    [TestCase(31ul, 3498, false)]
    [TestCase(31ul, 3499, true)]
    [TestCase(32ul, 2499, false)]
    [TestCase(32ul, 2500, true)]
    [TestCase(33ul, 2499, false)]
    [TestCase(33ul, 2500, true)]
    public async Task Due_time_includes_the_gossip_clock_allowance(ulong signatureSlot, int milliseconds, bool expected)
    {
        ManualClock clock = new();
        clock.SetSlot(signatureSlot, 0, milliseconds);
        await using LightClientGossip gossip = new(ConsensusTests.Spec with { GloasForkEpoch = 1 }, LimboLogs.Instance, clock);
        Assert.That(gossip.IsDue(signatureSlot), Is.EqualTo(expected));
        Assert.That(gossip.IsDue(ulong.MaxValue), Is.False);
    }

    private static LightClientFinalityUpdate Finality(LightClientUpdate update) => new()
    {
        AttestedHeader = update.AttestedHeader,
        FinalizedHeader = update.FinalizedHeader,
        FinalityBranch = update.FinalityBranch,
        SyncAggregate = update.SyncAggregate,
        SignatureSlot = update.SignatureSlot,
    };

    private static LightClientOptimisticUpdate Optimistic(LightClientUpdate update) => new()
    {
        AttestedHeader = update.AttestedHeader,
        SyncAggregate = update.SyncAggregate,
        SignatureSlot = update.SignatureSlot,
    };

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly ManualClock Clock = new();
        internal readonly LightClientGossip Gossip;
        internal readonly LightClientStore Store;
        internal readonly Dictionary<string, FakeTopic> Topics = new(StringComparer.Ordinal);

        internal Fixture()
        {
            Clock.SetSlot(10, 6);
            LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(1, 1);
            Store = new(ConsensusTests.Spec, SszRoots.HashTreeRoot(bootstrap.Header!.Beacon!), bootstrap, 10);
            Gossip = new(ConsensusTests.Spec, LimboLogs.Instance, Clock);
            Gossip.Start(GetTopic, (update, _) =>
            {
                ulong before = Store.FinalizedHeader.Beacon!.Slot;
                Store.Process(update, Slot());
                return Task.FromResult(Store.FinalizedHeader.Beacon!.Slot > before);
            }, (update, _) =>
            {
                ulong before = Store.OptimisticHeader.Beacon!.Slot;
                Store.Process(update, Slot());
                return Task.FromResult(Store.OptimisticHeader.Beacon!.Slot > before);
            }, CancellationToken.None);
        }

        internal ITopic GetTopic(string name)
        {
            if (!Topics.TryGetValue(name, out FakeTopic? topic)) Topics[name] = topic = new();
            return topic;
        }

        private ulong Slot() => ConsensusTests.Spec.GetSlotAtTime((ulong)Clock.GetUtcNow().ToUnixTimeSeconds());
        internal string Topic(string name) => GossipTopics.Topic(ForkDigest.Compute(ConsensusTests.Spec, 0), name);
        internal Task<MessageValidity> Handle(LightClientFinalityUpdate update) => Gossip.HandleAsync(Topic(GossipTopics.LightClientFinalityUpdate),
            Snappy.CompressToArray(LightClientWireCodec.EncodeFinality(update, ConsensusTests.Spec)), static _ => true, CancellationToken.None);
        internal Task<MessageValidity> Handle(LightClientOptimisticUpdate update) => Gossip.HandleAsync(Topic(GossipTopics.LightClientOptimisticUpdate),
            Snappy.CompressToArray(LightClientWireCodec.EncodeOptimistic(update, ConsensusTests.Spec)), static _ => true, CancellationToken.None);
        public ValueTask DisposeAsync() => Gossip.DisposeAsync();
    }

    private sealed class FakeTopic : ITopic
    {
        public event Action<PeerId, byte[]>? OnMessage { add { } remove { } }
        public bool IsSubscribed { get; private set; }
        public bool HasHandlers => false;
        internal int UnsubscribeCount { get; private set; }
        public void Subscribe() => IsSubscribed = true;
        public void Unsubscribe() { IsSubscribed = false; UnsubscribeCount++; }
        public void Publish(byte[] value) { }
        public void Publish(IMessage value) { }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void SetSlot(ulong slot, int seconds, int milliseconds = 0) => _now = DateTimeOffset.FromUnixTimeSeconds(
            (long)(ConsensusTests.Spec.GenesisTime + slot * ConsensusTests.Spec.SecondsPerSlot)).AddSeconds(seconds).AddMilliseconds(milliseconds);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NoopTimer();
        private sealed class NoopTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => default;
        }
    }
}
