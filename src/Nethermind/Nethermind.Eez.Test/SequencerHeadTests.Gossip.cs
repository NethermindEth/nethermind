// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Modules;
using Nethermind.Eez.Config;
using Nethermind.Eez.Execution;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Crypto;
using KeyType = Nethermind.Libp2p.Core.Dto.KeyType;
using Libp2pPublicKey = Nethermind.Libp2p.Core.Dto.PublicKey;
using Nethermind.Eez.Network;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;
using NSubstitute;
using GossipRpc = Nethermind.Libp2p.Protocols.Pubsub.Dto.Rpc;

namespace Nethermind.Eez.Test;

public partial class SequencerHeadTests
{
    [Test]
    public void Author_fields_are_rejected_without_caching_the_clean_message(
        [Values("from", "seqno", "signature", "key")] string field, [Values] bool empty)
    {
        SequencerHeadTracker tracker = new();
        SequencerGossipValidator validator = CreateGossipValidator(tracker);
        Message clean = new() { Topic = _codec.Topic, Data = ByteString.CopyFrom(Compressed) };
        Message signed = clean.Clone();
        ByteString value = empty ? ByteString.Empty : ByteString.CopyFrom([1]);
        switch (field)
        {
            case "from": signed.From = value; break;
            case "seqno": signed.Seqno = value; break;
            case "signature": signed.Signature = value; break;
            case "key": signed.Key = value; break;
        }

        MessageId signedId = validator.MessageId(signed);
        MessageId cleanId = validator.MessageId(clean);
        MessageValidity rejected = validator.Validate(signed);
        VerifiedSequencerHead[] afterRejected = tracker.Candidates();
        MessageValidity accepted = validator.Validate(clean);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(signedId, Is.EqualTo(cleanId));
            Assert.That(rejected, Is.EqualTo(MessageValidity.Throttled));
            Assert.That(afterRejected, Is.Empty);
            Assert.That(accepted, Is.EqualTo(MessageValidity.Accepted));
        }
    }

    [Test]
    public void Gossip_notifies_only_for_an_accepted_new_head()
    {
        SequencerGossipValidator validator = CreateGossipValidator(new SequencerHeadTracker());
        int notifications = 0;
        validator.HeadAccepted += () => notifications++;
        Message message = new() { Topic = _codec.Topic, Data = ByteString.CopyFrom(Compressed) };
        MessageValidity first = validator.Validate(message);
        MessageValidity repeated = validator.Validate(message);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.EqualTo(MessageValidity.Accepted));
            Assert.That(repeated, Is.EqualTo(MessageValidity.Ignored));
            Assert.That(notifications, Is.EqualTo(1));
        }
    }

    [Test]
    public void Foreign_topic_cannot_poison_the_expected_topic()
    {
        SequencerHeadTracker tracker = new();
        SequencerGossipValidator validator = CreateGossipValidator(tracker);
        Message message = new() { Topic = "/eez/1337/2/heads/0", Data = ByteString.CopyFrom(Compressed) };
        MessageValidity rejected = validator.Validate(message);
        VerifiedSequencerHead[] afterRejected = tracker.Candidates();
        message.Topic = _codec.Topic;
        MessageValidity accepted = validator.Validate(message);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rejected, Is.EqualTo(MessageValidity.Throttled));
            Assert.That(afterRejected, Is.Empty);
            Assert.That(accepted, Is.EqualTo(MessageValidity.Accepted));
        }
    }

    [Test]
    public async Task Frame_read_preserves_allowed_lengths_and_cancellation(
        [Values(0, 1, SequencerHeadCodec.MaxFrameSize)] int length)
    {
        IChannel inner = Substitute.For<IChannel>();
        IChannel channel = new SequencerFrameChannel(inner);
        using CancellationTokenSource cancellation = new();
        await channel.ReadAsync(length, ReadBlockingMode.WaitAny, cancellation.Token);
        await inner.Received(1).ReadAsync(length, ReadBlockingMode.WaitAny, cancellation.Token);
    }

    [Test]
    public async Task Frame_read_rejects_length_before_reading_the_payload(
        [Values(-1, SequencerHeadCodec.MaxFrameSize + 1, int.MaxValue)] int length)
    {
        IChannel inner = Substitute.For<IChannel>();
        IChannel channel = new SequencerFrameChannel(inner);
        Assert.That(async () => await channel.ReadAsync(length), Throws.TypeOf<InvalidDataException>());
        await inner.Received(1).CloseAsync();
        await inner.DidNotReceiveWithAnyArgs().ReadAsync(default);
    }

    [Test]
    public async Task Protobuf_size_is_checked_through_the_channel_interface_before_serialization(
        [Values(SequencerHeadCodec.MaxFrameSize + 1, int.MaxValue)] int length)
    {
        IChannel inner = Substitute.For<IChannel>();
        IChannel channel = new SequencerFrameChannel(inner);
        Assert.That(async () => await channel.WriteSizeAndProtobufAsync(new OversizedGossipMessage(length)),
            Throws.TypeOf<InvalidDataException>());
        await inner.Received(1).CloseAsync();
        await inner.DidNotReceiveWithAnyArgs().WriteAsync(default);
    }

    [Test]
    public async Task Protobuf_write_preserves_the_encoded_frame()
    {
        MemoryGossipChannel inner = new([]);
        IChannel channel = new SequencerFrameChannel(inner);
        Message message = new() { Topic = _codec.Topic, Data = ByteString.CopyFrom(Compressed) };
        await channel.WriteSizeAndProtobufAsync(message);
        using MemoryStream stream = new(inner.Written!);
        Message decoded = Message.Parser.ParseDelimitedFrom(stream);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded, Is.EqualTo(message));
            Assert.That(stream.Position, Is.EqualTo(stream.Length));
        }
    }

    [Test]
    public async Task Protobuf_write_accepts_the_exact_size_limit()
    {
        BytesValue message = new()
        {
            Value = ByteString.CopyFrom(new byte[SequencerHeadCodec.MaxFrameSize - 5])
        };
        Assert.That(message.CalculateSize(), Is.EqualTo(SequencerHeadCodec.MaxFrameSize));
        MemoryGossipChannel inner = new([]);
        IChannel channel = new SequencerFrameChannel(inner);
        await channel.WriteSizeAndProtobufAsync(message);
        Assert.That(inner.Written, Has.Length.EqualTo(SequencerHeadCodec.MaxFrameSize + 4));
    }

    [Test]
    public async Task Raw_frame_writes_enforce_the_prefix_allowance([Values] bool oversized)
    {
        MemoryGossipChannel inner = new([]);
        IChannel channel = new SequencerFrameChannel(inner);
        byte[] bytes = new byte[SequencerHeadCodec.MaxFrameSize + 5 + (oversized ? 1 : 0)];
        if (oversized)
        {
            Assert.That(async () => await channel.WriteAsync(new ReadOnlySequence<byte>(bytes)),
                Throws.TypeOf<InvalidDataException>());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(inner.Closed, Is.True);
                Assert.That(inner.Written, Is.Null);
            }
        }
        else
        {
            await channel.WriteAsync(new ReadOnlySequence<byte>(bytes));
            Assert.That(inner.Written, Is.EqualTo(bytes));
        }
    }

    [Test]
    public async Task Prefix_reader_preserves_supported_lengths_and_cancellation([Values] bool nonminimal)
    {
        IChannel inner = Substitute.For<IChannel>();
        int expected = nonminimal ? 1 : SequencerHeadCodec.MaxFrameSize;
        byte[] prefix = nonminimal ? [0x81, 0x00] : FramePrefix(expected);
        int offset = 0;
        using CancellationTokenSource cancellation = new();
        inner.ReadAsync(1, ReadBlockingMode.WaitAll, cancellation.Token).Returns(_ =>
            ValueTask.FromResult(new ReadResult { Result = IOResult.Ok, Data = new ReadOnlySequence<byte>(prefix.AsMemory(offset++, 1)) }));
        IChannel channel = new SequencerFrameChannel(inner);
        int length = await channel.ReadVarintAsync(cancellation.Token);
        Assert.That(length, Is.EqualTo(expected));
        await inner.Received(prefix.Length).ReadAsync(1, ReadBlockingMode.WaitAll, cancellation.Token);
    }

    [Test]
    public void Protobuf_reader_rejects_oversized_overflowing_and_unterminated_prefixes(
        [ValueSource(nameof(InvalidFramePrefixes))] byte[] prefix)
    {
        MemoryGossipChannel inner = new(prefix);
        IChannel channel = new SequencerFrameChannel(inner);
        Assert.That(async () => await channel.ReadPrefixedProtobufAsync(GossipRpc.Parser), Throws.TypeOf<InvalidDataException>());
        Assert.That(inner.Closed, Is.True);
    }

    [Test]
    public async Task Selector_passes_the_bounded_channel_to_the_negotiated_protocol([Values] bool listening)
    {
        using MemoryStream incoming = new();
        foreach (string line in new[] { "/multistream/1.0.0", "/meshsub/1.2.0" })
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
            incoming.Write(FramePrefix(bytes.Length));
            incoming.Write(bytes);
        }
        MemoryGossipChannel inner = new(incoming.ToArray());
        IProtocol protocol = Substitute.For<IProtocol>();
        protocol.Id.Returns("/meshsub/1.2.0");
        IConnectionContext context = Substitute.For<IConnectionContext>();
        context.SubProtocols.Returns([protocol]);
        SequencerGossipSelector selector = new(new MultistreamProtocol(), _codec);
        if (listening) await selector.ListenAsync(inner, context);
        else await selector.DialAsync(inner, context);
        await context.Received(1).Upgrade(Arg.Is<IChannel>(c => c is SequencerFrameChannel), protocol, null);
    }

    [Test]
    public void Malformed_heads_are_dropped_without_negative_cache_retention()
    {
        SequencerHeadTracker tracker = new();
        SequencerGossipValidator validator = CreateGossipValidator(tracker);
        MessageValidity verdict = validator.Validate(new Message { Topic = _codec.Topic, Data = ByteString.CopyFrom([0xff]) });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict, Is.EqualTo(MessageValidity.Throttled));
            Assert.That(tracker.Candidates(), Is.Empty);
        }
    }

    [Test]
    public void Signed_equivocation_is_rejected_without_replacing_the_candidate()
    {
        SequencerHeadTracker tracker = new();
        SequencerGossipValidator validator = CreateGossipValidator(tracker);
        Message first = new() { Topic = _codec.Topic, Data = ByteString.CopyFrom(Compressed) };
        Assert.That(validator.Validate(first), Is.EqualTo(MessageValidity.Accepted));
        Rlp[] header = HeaderFields();
        header[12] = Rlp.Encode("conflict"u8);
        Message conflict = new() { Topic = _codec.Topic, Data = ByteString.CopyFrom(EncodeBlock(header)) };
        MessageValidity verdict = validator.Validate(conflict);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict, Is.EqualTo(MessageValidity.Rejected));
            Assert.That(tracker.Candidates()[0].Claim, Is.EqualTo(Claim));
        }
    }

    [Test]
    public void Gossip_stack_retains_the_router_protocol_types_without_requiring_identify()
    {
        using ServiceProvider services = new ServiceCollection()
            .AddSingleton(_codec)
            .AddSingleton<PeerStore>()
            .AddSingleton<IProtocolStackSettings, ProtocolStackSettings>()
            .AddSingleton<MultiplexerSettings>()
            .AddSingleton<MultistreamProtocol>()
            .AddSingleton<PubsubSettings>()
            .AddSingleton<PubsubRouter>()
            .BuildServiceProvider();
        using SequencerPeerFactoryBuilder builder = new(services);
        IPeerFactory factory = builder.Build();
        IProtocolStackSettings stack = services.GetRequiredService<IProtocolStackSettings>();
        ProtocolRef[] protocols = [.. stack.Protocols!.Keys];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(factory, Is.TypeOf<PeerFactory>());
            Assert.That(protocols, Has.Some.Matches<ProtocolRef>(p => p?.Protocol.GetType() == typeof(GossipsubProtocolV12)));
            Assert.That(protocols, Has.Some.Matches<ProtocolRef>(p => p?.Protocol.GetType() == typeof(GossipsubProtocolV11)));
            Assert.That(protocols, Has.Some.Matches<ProtocolRef>(p => p?.Protocol.GetType() == typeof(GossipsubProtocol)));
            Assert.That(protocols, Has.None.Matches<ProtocolRef>(p => p?.Protocol is IdentifyProtocol or FloodsubProtocol));
            Assert.That(protocols, Has.Some.Matches<ProtocolRef>(p => p?.Protocol is SequencerGossipSelector));
        }
    }

    [Test]
    public async Task Signed_head_crosses_TCP_Noise_Yamux_and_the_registered_gossip_listener()
    {
        EezConfig config = new()
        {
            FollowerEnabled = true,
            PeerSyncEnabled = true,
            RegistryAddress = Registry.ToString(),
            SequencerP2PSigner = Signer.ToString(),
            RollupId = 1,
            L1RpcUrl = "http://127.0.0.1:8545",
            SequencerP2PHost = "127.0.0.1",
            SequencerP2PPort = 0
        };
        await using IContainer container = GossipContainer(config);
        EezPeerNetwork network = container.Resolve<EezPeerNetwork>();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        await network.Start(deadline.Token);
        WireHeadSender wire = new();
        await using ServiceProvider services = new ServiceCollection()
            .AddSingleton<PeerStore>()
            .AddSingleton<IProtocolStackSettings, ProtocolStackSettings>()
            .AddSingleton<MultiplexerSettings>()
            .AddSingleton<MultistreamProtocol>()
            .AddSingleton(wire)
            .BuildServiceProvider();
        await using WireHeadFactoryBuilder builder = new(services);
        await using ILocalPeer sender = builder.Build().Create(new Identity());
        ISession session = await sender.DialAsync(network.ListenAddresses[0], deadline.Token);
        Task stream = session.DialAsync(wire, deadline.Token);
        try
        {
            IChannel channel = await wire.Ready.Task.WaitAsync(deadline.Token);
            GossipRpc rpc = new()
            {
                Publish = { new Message { Topic = _codec.Topic, Data = ByteString.CopyFrom(Compressed) } }
            };
            await channel.WriteSizeAndProtobufAsync(rpc).AsTask().WaitAsync(deadline.Token);
            await network.WaitForHead(TimeSpan.FromSeconds(20), deadline.Token);
            Assert.That(network.Tracker.Candidates().Select(static head => head.Claim), Is.EqualTo(new[] { Claim }));
        }
        finally
        {
            await session.DisconnectAsync();
            try { await stream.WaitAsync(deadline.Token); }
            catch (OperationCanceledException) when (stream.IsCanceled) { }
        }
    }

    [Test]
    public async Task Accepted_head_wakes_the_driver_but_invalid_or_repeated_messages_do_not([Values] bool malformed)
    {
        EezConfig config = new()
        {
            FollowerEnabled = true,
            PeerSyncEnabled = true,
            RegistryAddress = Registry.ToString(),
            SequencerP2PSigner = Signer.ToString(),
            RollupId = 1,
            L1RpcUrl = "http://127.0.0.1:8545"
        };
        await using IContainer container = GossipContainer(config);
        EezPeerNetwork network = container.Resolve<EezPeerNetwork>();
        Message head = new() { Topic = _codec.Topic, Data = ByteString.CopyFrom(Compressed) };
        Assert.That(network.Validate(head), Is.EqualTo(MessageValidity.Accepted));
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(20));
        await network.WaitForHead(TimeSpan.FromDays(1), cancellation.Token);
        if (malformed) head.Data = ByteString.CopyFrom([0xff]);
        Assert.That(network.Validate(head), Is.EqualTo(malformed ? MessageValidity.Throttled : MessageValidity.Ignored));
        Task waiting = network.WaitForHead(TimeSpan.FromDays(1), cancellation.Token);
        Assert.That(waiting.IsCompleted, Is.False);
        await cancellation.CancelAsync();
        Assert.That(async () => await waiting, Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public void Overlay_identity_matches_the_node_key_with_the_high_bit_set([Values(1, 128, 255)] int firstByte)
    {
        byte[] bytes = new byte[32];
        bytes[0] = (byte)firstByte;
        bytes[^1] = 1;
        using PrivateKey key = new(bytes);
        Identity identity = new(bytes, KeyType.Secp256K1);
        Identity advertised = new(new Libp2pPublicKey
        {
            Type = KeyType.Secp256K1,
            Data = ByteString.CopyFrom(key.CompressedPublicKey.Bytes)
        });
        byte[] payload = [1, 2, 3];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(identity.PublicKey.Data.ToByteArray(), Is.EqualTo(key.CompressedPublicKey.Bytes));
            Assert.That(identity.PeerId, Is.EqualTo(advertised.PeerId));
            Assert.That(advertised.VerifySignature(payload, identity.Sign(payload)), Is.True);
        }
    }

    [Test]
    public async Task Control_ids_have_the_EEZ_message_id_length(
        [Values("ihave", "iwant", "idontwant")] string kind, [Values(0, 19, 20, 21, 65536)] int length)
    {
        ByteString id = ByteString.CopyFrom(new byte[length]);
        GossipRpc rpc = new() { Control = new ControlMessage() };
        switch (kind)
        {
            case "ihave": rpc.Control.Ihave.Add(new ControlIHave { TopicID = _codec.Topic, MessageIDs = { id } }); break;
            case "iwant": rpc.Control.Iwant.Add(new ControlIWant { MessageIDs = { id } }); break;
            case "idontwant": rpc.Control.Idontwant.Add(new ControlIDontWant { MessageIDs = { id } }); break;
        }
        MemoryGossipChannel inner = EncodeRpc(rpc);
        IChannel channel = new SequencerFrameChannel(inner, _codec.Topic);
        if (length == 20)
            Assert.That(await channel.ReadPrefixedProtobufAsync(GossipRpc.Parser), Is.EqualTo(rpc));
        else
        {
            Assert.That(async () => await channel.ReadPrefixedProtobufAsync(GossipRpc.Parser),
                Throws.TypeOf<InvalidDataException>());
            Assert.That(inner.Closed, Is.True);
        }
    }

    [Test]
    public void Foreign_control_topics_are_rejected_before_routing(
        [Values("subscription", "ihave", "graft", "prune")] string kind)
    {
        const string foreign = "/eez/1337/2/heads/0";
        GossipRpc rpc = new() { Control = new ControlMessage() };
        switch (kind)
        {
            case "subscription": rpc.Subscriptions.Add(new GossipRpc.Types.SubOpts { Subscribe = true, Topicid = foreign }); break;
            case "ihave": rpc.Control.Ihave.Add(new ControlIHave { TopicID = foreign }); break;
            case "graft": rpc.Control.Graft.Add(new ControlGraft { TopicID = foreign }); break;
            case "prune": rpc.Control.Prune.Add(new ControlPrune { TopicID = foreign }); break;
        }
        MemoryGossipChannel inner = EncodeRpc(rpc);
        IChannel channel = new SequencerFrameChannel(inner, _codec.Topic);
        Assert.That(async () => await channel.ReadPrefixedProtobufAsync(GossipRpc.Parser),
            Throws.TypeOf<InvalidDataException>());
        Assert.That(inner.Closed, Is.True);
    }

    private static IContainer GossipContainer(EezConfig config) => new ContainerBuilder()
        .AddModule(new TestNethermindModule())
        .AddSingleton<IEezConfig>(config)
        .AddSingleton<ISpecProvider>(new TestSpecProvider(London.Instance) { ChainId = 1337 })
        .AddSingleton<ITimestamper>(new ManualTimestamper(DateTimeOffset.FromUnixTimeMilliseconds((long)Claim.EpochMs).UtcDateTime))
        .AddModule(new EezModule(config, EezChainRules.Rollup0))
        .Build();

    private static MemoryGossipChannel EncodeRpc(GossipRpc rpc)
    {
        using MemoryStream stream = new();
        rpc.WriteDelimitedTo(stream);
        return new MemoryGossipChannel(stream.ToArray());
    }

    private SequencerGossipValidator CreateGossipValidator(SequencerHeadTracker tracker) =>
        new(_codec, tracker, new ManualTimestamper(DateTimeOffset.FromUnixTimeMilliseconds((long)Claim.EpochMs).UtcDateTime));

    private static IEnumerable<byte[]> InvalidFramePrefixes() =>
    [
        FramePrefix(SequencerHeadCodec.MaxFrameSize + 1),
        [0x80, 0x80, 0x80, 0x80, 0x10],
        [0x80, 0x80, 0x80, 0x80, 0x80],
    ];

    private static byte[] FramePrefix(int length)
    {
        byte[] prefix = new byte[5];
        int offset = 0;
        VarInt.Encode(length, prefix, ref offset);
        return prefix[..offset];
    }

    private sealed class WireHeadFactoryBuilder(IServiceProvider services)
        : PeerFactoryBuilderBase<WireHeadFactoryBuilder, PeerFactory>(services)
    {
        protected override ProtocolRef[] BuildStack(IEnumerable<ProtocolRef> additionalProtocols)
        {
            ProtocolRef tcp = Get<IpTcpProtocol>();
            Connect([tcp], [Get<MultistreamProtocol>()], [Get<NoiseProtocol>()], [Get<MultistreamProtocol>()],
                [Get<YamuxProtocol>()], [Get<MultistreamProtocol>()], [Get<WireHeadSender>()]);
            return [tcp];
        }
    }

    private sealed class WireHeadSender : ISessionProtocol
    {
        public string Id => PubsubRouter.GossipsubProtocolVersionV12;
        internal TaskCompletionSource<IChannel> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task DialAsync(IChannel channel, ISessionContext context)
        {
            Ready.TrySetResult(channel);
            await channel;
        }

        public async Task ListenAsync(IChannel channel, ISessionContext context) => await channel;
    }

    private sealed class MemoryGossipChannel(byte[] input) : IChannel
    {
        private int _position;
        public byte[]? Written { get; private set; }
        public bool Closed { get; private set; }

        public ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (length > input.Length - _position) return ValueTask.FromResult(ReadResult.Ended);
            ReadOnlySequence<byte> data = new(input.AsMemory(_position, length));
            _position += length;
            return ValueTask.FromResult(new ReadResult { Result = IOResult.Ok, Data = data });
        }

        public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Written = bytes.ToArray();
            return ValueTask.FromResult(IOResult.Ok);
        }

        public ValueTask AbortAsync() => CloseAsync();

        public ValueTask CloseAsync()
        {
            Closed = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => ValueTask.FromResult(IOResult.Ok);
        public TaskAwaiter GetAwaiter() => Task.CompletedTask.GetAwaiter();
    }

    private sealed class OversizedGossipMessage(int length) : IMessage<OversizedGossipMessage>
    {
        public MessageDescriptor Descriptor => throw new NotSupportedException();
        public int CalculateSize() => length;
        public OversizedGossipMessage Clone() => throw new NotSupportedException();
        public bool Equals(OversizedGossipMessage? other) => ReferenceEquals(this, other);
        public void MergeFrom(OversizedGossipMessage message) => throw new NotSupportedException();
        public void MergeFrom(CodedInputStream input) => throw new NotSupportedException();
        public void WriteTo(CodedOutputStream output) => throw new NotSupportedException();
    }
}
