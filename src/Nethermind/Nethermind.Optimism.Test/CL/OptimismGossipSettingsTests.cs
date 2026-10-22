// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Google.Protobuf;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Optimism.CL;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Protocols;
using Nethermind.Network.Libp2p;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using Nethermind.Optimism.CL.P2P;
using NSubstitute;
using NUnit.Framework;
using GossipRpc = Nethermind.Libp2p.Protocols.Pubsub.Dto.Rpc;

namespace Nethermind.Optimism.Test.CL;

public class OptimismGossipSettingsTests
{
    private const string BlocksTopic = "/optimism/10/2/blocks";

    [Test]
    public void Every_gossip_version_selects_a_bounded_listener_before_the_library_dialer()
    {
        using ServiceProvider services = new ServiceCollection()
            .AddLibp2p(OptimismCLP2P.ConfigureGossipStack)
            .AddSingleton(OptimismCLP2P.CreatePubsubSettings())
            .BuildServiceProvider();
        _ = services.GetRequiredService<IPeerFactory>();
        ProtocolRef[] protocols = services.GetRequiredService<IProtocolStackSettings>().Protocols!.Values
            .Single(protocols => protocols.Any(protocol => protocol.Protocol is BoundedGossipProtocol));
        IGrouping<string, ProtocolRef>[] gossip = protocols.Where(protocol => protocol.Protocol is BoundedGossipProtocol or PubsubProtocol)
            .GroupBy(protocol => protocol.Id).ToArray();

        Assert.That(gossip, Has.Length.EqualTo(5));
        foreach (IGrouping<string, ProtocolRef> version in gossip)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(version.First().Protocol, Is.TypeOf<BoundedGossipProtocol>());
                Assert.That(version.Last().Protocol, Is.InstanceOf<PubsubProtocol>());
            }
        }
    }

    [Test]
    public async Task Gossip_streams_share_a_global_and_per_peer_allowance_before_any_rpc_body_read()
    {
        GossipStreamBudget budget = new();
        ISessionProtocol inner = Substitute.For<ISessionProtocol>();
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int admitted = 0;
        inner.ListenAsync(Arg.Any<IChannel>(), Arg.Any<ISessionContext>()).Returns(async call =>
        {
            try
            {
                await call.Arg<IChannel>().ReadVarintUlongAsync(CancellationToken.None);
                Interlocked.Increment(ref admitted);
                await done.Task;
            }
            catch (ChannelClosedException) { }
        });
        BoundedGossipProtocol first = new(inner, budget);
        BoundedGossipProtocol otherVersion = new(inner, budget);
        ISessionContext[] peers = Enumerable.Range(0, GossipStreamBudget.MaxStreams + 1).Select(_ => Session()).ToArray();
        Task[] listening = new Task[GossipStreamBudget.MaxStreams + 2];
        Array.Fill(listening, Task.CompletedTask);
        listening[0] = first.ListenAsync(ReadyChannel(), peers[0]);
        try
        {
            IChannel duplicate = ReadyChannel();
            listening[^2] = otherVersion.ListenAsync(duplicate, peers[0]);
            Assert.That(listening[^2].IsCompleted, Is.False);
            await duplicate.DidNotReceive().CloseAsync();
            Assert.That(admitted, Is.EqualTo(1));

            Parallel.For(1, GossipStreamBudget.MaxStreams, i => listening[i] = first.ListenAsync(ReadyChannel(), peers[i]));
            IChannel excess = ReadyChannel();
            listening[^1] = otherVersion.ListenAsync(excess, peers[^1]);

            Assert.That(listening[^1].IsCompleted, Is.False);
            await excess.DidNotReceive().CloseAsync();
            Assert.That(admitted, Is.EqualTo(GossipStreamBudget.MaxStreams));
        }
        finally
        {
            done.SetResult();
            await Task.WhenAll(listening);
        }
        Assert.That(admitted, Is.EqualTo(GossipStreamBudget.MaxStreams + 2));
    }

    [Test]
    [CancelAfter(5_000)]
    public async Task A_stalled_rpc_body_times_out_and_releases_its_allowance(CancellationToken token)
    {
        GossipStreamBudget budget = new();
        ISessionProtocol inner = Substitute.For<ISessionProtocol>();
        inner.ListenAsync(Arg.Any<IChannel>(), Arg.Any<ISessionContext>()).Returns(async call =>
        {
            await call.Arg<IChannel>().ReadPrefixedProtobufAsync(GossipRpc.Parser, 1024, token);
        });
        BoundedGossipProtocol protocol = new(inner, budget, TimeSpan.FromMilliseconds(100));
        ISessionContext peer = Session();
        Channel channel = new();
        Task listener = protocol.ListenAsync(channel, peer);
        try
        {
            await channel.Reverse.WriteVarintAsync(1024).AsTask().WaitAsync(token);
            await Assert.ThrowsAsync<ChannelClosedException>(async () => await listener.WaitAsync(token));
            Assert.That(budget.AcquireAsync(peer.State.RemotePeerId!, token).IsCompletedSuccessfully, Is.True);
            budget.Release(peer.State.RemotePeerId!);
        }
        finally
        {
            await channel.CloseAsync();
        }
    }

    [Test]
    public async Task Gossip_allowance_is_released_when_a_listener_ends([Values(null, typeof(InvalidOperationException), typeof(OperationCanceledException))] Type? error)
    {
        GossipStreamBudget budget = new();
        ISessionProtocol inner = Substitute.For<ISessionProtocol>();
        Exception? failure = error is null ? null : (Exception)Activator.CreateInstance(error)!;
        inner.ListenAsync(Arg.Any<IChannel>(), Arg.Any<ISessionContext>()).Returns(async call =>
        {
            await call.Arg<IChannel>().ReadVarintUlongAsync(CancellationToken.None);
            if (failure is not null) throw failure;
        });
        BoundedGossipProtocol protocol = new(inner, budget);
        ISessionContext peer = Session();
        IChannel channel = ReadyChannel();

        if (failure is null) await protocol.ListenAsync(channel, peer);
        else await Assert.ThrowsAsync(error!, () => protocol.ListenAsync(channel, peer));

        failure = null;
        await protocol.ListenAsync(channel, peer);
        await inner.Received(2).ListenAsync(Arg.Any<IChannel>(), peer);
        await channel.DidNotReceive().CloseAsync();
    }

    [Test]
    [CancelAfter(5_000)]
    public async Task An_idle_duplicate_stream_does_not_reserve_the_rpc_allowance(CancellationToken token)
    {
        GossipStreamBudget budget = new();
        ISessionProtocol inner = Substitute.For<ISessionProtocol>();
        TaskCompletionSource<GossipRpc> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        inner.ListenAsync(Arg.Any<IChannel>(), Arg.Any<ISessionContext>()).Returns(async call =>
        {
            try
            {
                received.TrySetResult(await call.Arg<IChannel>().ReadPrefixedProtobufAsync(GossipRpc.Parser, 1024, token));
                await done.Task.WaitAsync(token);
            }
            catch (ChannelClosedException) { }
        });
        BoundedGossipProtocol protocol = new(inner, budget);
        ISessionContext peer = Session();
        Channel idle = new();
        Channel active = new();
        Task idleListener = protocol.ListenAsync(idle, peer);
        Task activeListener = protocol.ListenAsync(active, peer);
        GossipRpc rpc = new() { Subscriptions = { new GossipRpc.Types.SubOpts { Topicid = BlocksTopic, Subscribe = true } } };
        try
        {
            await active.Reverse.WriteSizeAndProtobufAsync(rpc).AsTask().WaitAsync(token);
            Assert.That(await received.Task.WaitAsync(token), Is.EqualTo(rpc));
            Assert.That(idleListener.IsCompleted, Is.False);
            await idle.CloseAsync();
            await idleListener.WaitAsync(token);
            using CancellationTokenSource waiting = new();
            Task acquiring = budget.AcquireAsync(peer.State.RemotePeerId!, waiting.Token);
            Assert.That(acquiring.IsCompleted, Is.False, "closing an idle stream must not release the active stream's allowance");
            await waiting.CancelAsync();
            await Assert.CatchAsync<OperationCanceledException>(() => acquiring);
        }
        finally
        {
            done.TrySetResult();
            await idle.CloseAsync();
            await active.CloseAsync();
            await Task.WhenAll(idleListener, activeListener);
        }
    }

    private static IChannel ReadyChannel()
    {
        IChannel channel = Substitute.For<IChannel>();
        channel.ReadVarintUlongAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(1UL));
        return channel;
    }

    [Test]
    [CancelAfter(5_000)]
    public async Task Idle_gossip_peers_do_not_hold_the_global_rpc_allowance(CancellationToken token)
    {
        GossipStreamBudget budget = new();
        ISessionProtocol inner = Substitute.For<ISessionProtocol>();
        int processed = 0;
        TaskCompletionSource allProcessed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        inner.ListenAsync(Arg.Any<IChannel>(), Arg.Any<ISessionContext>()).Returns(async call =>
        {
            try
            {
                IChannel channel = call.Arg<IChannel>();
                await channel.ReadPrefixedProtobufAsync(GossipRpc.Parser, 1024, token);
                if (Interlocked.Increment(ref processed) == GossipStreamBudget.MaxStreams + 1) allProcessed.TrySetResult();
                await channel.ReadVarintUlongAsync(token);
            }
            catch (ChannelClosedException) { }
        });
        BoundedGossipProtocol protocol = new(inner, budget);
        Channel[] channels = Enumerable.Range(0, GossipStreamBudget.MaxStreams + 1).Select(_ => new Channel()).ToArray();
        Task[] listeners = channels.Select(channel => protocol.ListenAsync(channel, Session())).ToArray();
        GossipRpc rpc = new() { Subscriptions = { new GossipRpc.Types.SubOpts { Topicid = BlocksTopic, Subscribe = true } } };
        try
        {
            foreach (Channel channel in channels)
                await channel.Reverse.WriteSizeAndProtobufAsync(rpc).AsTask().WaitAsync(token);
            await allProcessed.Task.WaitAsync(token);
            Assert.That(processed, Is.EqualTo(channels.Length));
        }
        finally
        {
            foreach (Channel channel in channels) await channel.CloseAsync();
            await Task.WhenAll(listeners);
        }
    }

    private static ISessionContext Session()
    {
        Identity identity = new();
        ISessionContext context = Substitute.For<ISessionContext>();
        context.State.Returns(new Nethermind.Libp2p.Core.State { RemoteAddress = Multiaddress.Decode($"/ip4/127.0.0.1/tcp/9222/p2p/{identity.PeerId}") });
        return context;
    }

    [TestCase("/ip4/10.0.0.1/tcp/9222/p2p/16Uiu2HAmRvz3gCpQuMeRxEz1F8B8EXHE9q9V1VE6pMVQuRWUt2iW", true, TestName = "An IPv4 address")]
    [TestCase("/dns4/sequencer.example/tcp/9222/p2p/16Uiu2HAmRvz3gCpQuMeRxEz1F8B8EXHE9q9V1VE6pMVQuRWUt2iW", true, TestName = "A DNS name")]
    [TestCase("/dnsaddr/sequencer.example/p2p/16Uiu2HAmRvz3gCpQuMeRxEz1F8B8EXHE9q9V1VE6pMVQuRWUt2iW", true, TestName = "A dnsaddr name")]
    [TestCase("/ip4/10.0.0.1/tcp/9222/dnsaddr/sequencer.example/p2p/16Uiu2HAmRvz3gCpQuMeRxEz1F8B8EXHE9q9V1VE6pMVQuRWUt2iW", false, TestName = "A dnsaddr name after an address")]
    [TestCase("/ip4/10.0.0.1/udp/9222/p2p/16Uiu2HAmRvz3gCpQuMeRxEz1F8B8EXHE9q9V1VE6pMVQuRWUt2iW", false, TestName = "A transport other than TCP")]
    [TestCase("/ip4/10.0.0.1/tcp/9222", false, TestName = "No peer id")]
    [TestCase("not a multiaddr", false, TestName = "Not a multiaddr")]
    public void Static_peers_are_checked_at_startup(string node, bool dialable)
    {
        TestLogger logger = new();
        OptimismCLP2P Create() => new(Substitute.For<IExecutionEngineManager>(), 10, ["/ip4/10.0.0.2/tcp/9222/p2p/16Uiu2HAmRvz3gCpQuMeRxEz1F8B8EXHE9q9V1VE6pMVQuRWUt2iW", node], new OptimismConfig(), Address.Zero,
            Substitute.For<ITimestamper>(), Substitute.For<IIPResolver>(), new OneLoggerLogManager(new ILogger(logger)));

        // An entry no dial reaches can be the sequencer, so startup fails instead of running without its unsafe blocks.
        if (!dialable)
        {
            Assert.That(Create, Throws.InstanceOf<InvalidConfigurationException>());
            Assert.That(logger.LogList.Where(line => line.Contains(node)), Has.Exactly(1).Contains("is skipped"));
            return;
        }

        using OptimismCLP2P p2p = Create();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(p2p.StaticPeersForTest.Select(static peer => peer.ToString()), Is.EqualTo(new[] { "/ip4/10.0.0.2/tcp/9222/p2p/16Uiu2HAmRvz3gCpQuMeRxEz1F8B8EXHE9q9V1VE6pMVQuRWUt2iW", node }));
            Assert.That(logger.LogList.Where(line => line.Contains(node)), Is.Empty);
        }
    }

    // Expected IDs hash domain, little-endian topic length, topic, and decoded or raw data, taking the first 20 bytes.
    [TestCase("0x051068656c6c6f", "0x0089d50f80a1e1668e93fef3bd0d075261438c71", TestName = "Valid snappy hashes the decoded data")]
    [TestCase("0xffffffff", "0xd8920a2d23d659830adbab71431e706fbba03ab8", TestName = "Invalid snappy hashes the raw data")]
    public void Message_id_is_the_op_stack_gossip_id(string data, string expected)
    {
        PubsubSettings settings = OptimismCLP2P.CreatePubsubSettings();

        MessageId id = settings.GetMessageId(new Message { Topic = BlocksTopic, Data = ByteString.CopyFrom(Bytes.FromHexString(data)) });

        Assert.That(id.Bytes, Is.EqualTo(Bytes.FromHexString(expected)));
    }

    [Test]
    public void Block_gossip_uses_op_stack_limits_and_penalizes_invalid_messages_without_quiet_mesh_penalties()
    {
        PubsubSettings settings = OptimismCLP2P.CreatePubsubSettings();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(settings.MaxRpcBytes, Is.EqualTo(Eth2MessageId.MaxMessageSize).And.GreaterThan(10 * 1024 * 1024));
            Assert.That(settings.MaxIwantResponseBytes, Is.EqualTo(Eth2MessageId.MaxMessageSize));
            PeerScore score = new(settings);
            TopicScore topic = score.GetTopicScore(BlocksTopic);
            topic.MeshJoinTime = DateTime.UtcNow.AddMinutes(-1);
            Assert.That(score.GetScore([]), Is.Zero);
            topic.RecordInvalidMessage();
            Assert.That(score.GetScore([]), Is.Negative);
        }
    }
}
