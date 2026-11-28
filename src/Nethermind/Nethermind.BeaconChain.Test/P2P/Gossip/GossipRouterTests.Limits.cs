// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Reflection;
using Autofac;
using Google.Protobuf;
using Multiformats.Address;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using Nethermind.Network.Libp2p;

namespace Nethermind.BeaconChain.Test.P2P.Gossip;

public partial class GossipRouterTests
{
    private static readonly string ColumnTopic = GossipTopics.Topic(ForkDigest.Compute(Spec, Spec.GetEpoch(CurrentSlot)), GossipTopics.DataColumnSidecarTopicName(5));

    // Every write is held until all copies are queued, as on any real link: a max-size frame takes about 100 ms at 1 Gbit/s.
    [Test]
    public async Task Largest_legal_message_is_forwarded_to_every_mesh_peer_up_to_d_high()
    {
        PubsubSettings settings = await CreateBeaconPubsubSettings();
        using PubsubRouter pubsub = new(new PeerStore(), settings);
        pubsub.VerifyMessage = static (_, _) => MessageValidity.Accepted;
        pubsub.GetTopic(BlockTopic);
        HeldPeer sender = await HeldPeer.Connect(pubsub, BlockTopic);
        List<HeldPeer> mesh = [];
        for (int i = 0; i < settings.HighestDegree; i++)
        {
            mesh.Add(await HeldPeer.Connect(pubsub, BlockTopic));
        }

        ((IRoutingStateContainer)pubsub).Mesh[BlockTopic].UnionWith(mesh.Select(static peer => peer.Id));
        TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        mesh.ForEach(peer => peer.Released = released.Task);

        await sender.Receive(new Rpc { Publish = { RandomMessage(BlockTopic, Eth2MessageId.MaxCompressedGossipSize, seed: 1) } });
        released.SetResult();
        await FlushOutboundAsync(pubsub);

        Assert.That(mesh.Count(static peer => peer.LargeFrames == 1), Is.EqualTo(settings.HighestDegree));
    }

    // Hundreds of small frames can build up during a short normal burst; the next required subscription frame must still fit.
    [Test]
    public async Task Mesh_peers_with_hundreds_of_messages_queued_still_get_our_subscription_change([Values] bool subscribe)
    {
        PubsubSettings settings = await CreateBeaconPubsubSettings();
        using PubsubRouter pubsub = new(new PeerStore(), settings);
        pubsub.VerifyMessage = static (_, _) => MessageValidity.Accepted;
        ITopic topic = pubsub.GetTopic(BlockTopic);
        HeldPeer sender = await HeldPeer.Connect(pubsub, BlockTopic);
        List<HeldPeer> mesh = [];
        for (int i = 0; i < settings.HighestDegree; i++)
        {
            mesh.Add(await HeldPeer.Connect(pubsub, BlockTopic));
        }

        ((IRoutingStateContainer)pubsub).Mesh[BlockTopic].UnionWith(mesh.Select(static peer => peer.Id));
        TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        mesh.ForEach(peer => peer.Released = released.Task);
        const int queued = 255;
        for (int i = 0; i < queued; i++)
        {
            await sender.Receive(new Rpc { Publish = { RandomMessage(BlockTopic, 300, seed: i) } });
        }

        if (subscribe)
        {
            pubsub.GetTopic(ColumnTopic);
        }
        else
        {
            topic.Unsubscribe();
        }

        released.SetResult();
        await FlushOutboundAsync(pubsub);

        string changed = subscribe ? ColumnTopic : BlockTopic;
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(mesh.All(peer => peer.Sent.Sum(static rpc => rpc.Publish.Count) == queued), Is.True, "every queued message is written");
        Assert.That(mesh.All(peer => peer.Sent.Any(rpc => rpc.Subscriptions.Any(sub => sub.Topicid == changed && sub.Subscribe == subscribe))),
            Is.True, "each peer is kept and gets the subscription change");
    }

    // An RPC that finds no room in the incoming budget while message ids are computed is skipped before validation.
    [Test]
    [CancelAfter(30_000)]
    public async Task Gossip_arriving_while_a_max_size_rpc_is_being_identified_is_validated([Values] bool maxSize, CancellationToken token)
    {
        PubsubSettings settings = await CreateBeaconPubsubSettings();
        Func<Message, MessageId> computeId = settings.GetMessageId;
        TaskCompletionSource identifying = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim identified = new();
        int held = 0;
        settings.GetMessageId = message =>
        {
            if (message.Data.Length == Eth2MessageId.MaxCompressedGossipSize && Interlocked.Exchange(ref held, 1) == 0)
            {
                identifying.SetResult();
                identified.Wait(token);
            }

            return computeId(message);
        };
        using PubsubRouter pubsub = new(new PeerStore(), settings);
        ConcurrentBag<int> validated = [];
        pubsub.VerifyMessage = (_, message) =>
        {
            validated.Add(message.Data.Length);
            return MessageValidity.Accepted;
        };
        pubsub.GetTopic(BlockTopic);
        HeldPeer first = await HeldPeer.Connect(pubsub, BlockTopic);
        HeldPeer second = await HeldPeer.Connect(pubsub, BlockTopic);
        int length = maxSize ? Eth2MessageId.MaxCompressedGossipSize : 6 * 1024 * 1024;

        Task identifyingFirst = Task.Run(() => first.Receive(new Rpc { Publish = { RandomMessage(BlockTopic, Eth2MessageId.MaxCompressedGossipSize, seed: 1) } }), token);
        await identifying.Task.WaitAsync(token);
        Task arriving = second.Receive(new Rpc { Publish = { RandomMessage(BlockTopic, length, seed: 2) } });
        identified.Set();
        await Task.WhenAll(identifyingFirst, arriving).WaitAsync(token);

        Assert.That(validated.Count(validatedLength => validatedLength == length), Is.EqualTo(maxSize ? 2 : 1));
    }

    private static async Task<PubsubSettings> CreateBeaconPubsubSettings()
    {
        using IContainer container = BeaconChainTestContainer.Builder().Build();
        await using BeaconP2P p2p = container.Resolve<BeaconP2P>();
        return p2p.PubsubSettingsForTest;
    }

    /// <summary>Random data behind a snappy length header past GOSSIP_MAX_SIZE, so the message id hashes the raw bytes and differs by seed.</summary>
    private static Message RandomMessage(string topic, int length, int seed)
    {
        byte[] data = new byte[length];
        new Random(seed).NextBytes(data);
        data.AsSpan(0, 4).Fill(0xFF);
        data[4] = 0x0F;
        return new Message { Topic = topic, Data = UnsafeByteOperations.UnsafeWrap(data) };
    }

    /// <summary>A gossipsub v1.2 peer on an in-memory link whose writes wait for <see cref="Released"/>, as a real link does while it sends earlier bytes.</summary>
    private sealed class HeldPeer
    {
        private readonly PubsubRouter _pubsub;
        private int _largeFrames;

        private HeldPeer(PubsubRouter pubsub) => _pubsub = pubsub;

        public PeerId Id { get; } = new Identity(privateKey: null, Nethermind.Libp2p.Core.Dto.KeyType.Secp256K1).PeerId;
        public Task Released { get; set; } = Task.CompletedTask;
        /// <summary>Written frames of at least 1 MiB; smaller ones are parsed into <see cref="Sent"/>.</summary>
        public int LargeFrames => Volatile.Read(ref _largeFrames);
        public ConcurrentQueue<Rpc> Sent { get; } = new();

        public static async Task<HeldPeer> Connect(PubsubRouter pubsub, string topicId)
        {
            HeldPeer peer = new(pubsub);
            TaskCompletionSource dial = new(TaskCreationOptions.RunContinuationsAsynchronously);
            typeof(PubsubRouter).GetMethod("OutboundConnection", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(pubsub,
                [Multiaddress.Decode($"/ip4/127.0.0.1/tcp/9000/p2p/{peer.Id}"), PubsubRouter.GossipsubProtocolVersionV12, dial.Task,
                    (Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<IOResult>>)peer.WriteAsync,
                    (Func<ValueTask>)(static () => ValueTask.CompletedTask), (Func<Task>)(static () => Task.CompletedTask)]);
            await peer.Receive(new Rpc { Subscriptions = { new Rpc.Types.SubOpts { Topicid = topicId, Subscribe = true } } });
            await FlushOutboundAsync(pubsub);
            return peer;
        }

        /// <summary>Delivers <paramref name="rpc"/> from this peer; outbound frames are not flushed, so held writes stay queued.</summary>
        public Task Receive(Rpc rpc) =>
            (Task)typeof(PubsubRouter).GetMethod("OnRpcAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_pubsub, [Id, rpc, null, true, CancellationToken.None])!;

        private async ValueTask<IOResult> WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken token)
        {
            Record(frame);
            await Released.WaitAsync(token);
            return IOResult.Ok;
        }

        private void Record(ReadOnlyMemory<byte> frame)
        {
            if (frame.Length >= 1024 * 1024)
            {
                Interlocked.Increment(ref _largeFrames);
                return;
            }

            int offset = 0;
            while ((frame.Span[offset++] & 0x80) != 0) { }
            Sent.Enqueue(Rpc.Parser.ParseFrom(frame.Span[offset..]));
        }
    }
}
