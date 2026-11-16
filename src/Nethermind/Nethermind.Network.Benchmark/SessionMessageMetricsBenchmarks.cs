// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using DotNetty.Transport.Channels;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
using Nethermind.Network.Rlpx;
using Nethermind.Stats.Model;
using NSubstitute;

namespace Nethermind.Network.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class SessionMessageMetricsBenchmarks
{
    private const int BatchSize = 65536;
    private const int P2PSpaceSize = 0x10;
    private const int EthSpaceSize = 0x11;
    private const int EthVersion = 68;
    private const int MessageSize = 256;
    private const byte AdaptiveId = P2PSpaceSize + Eth65MessageCode.NewPooledTransactionHashes;

    private Action<int> _receive = null!;
    private Action<int> _deliver = null!;

    [Params(1, 8, 16)]
    public int Peers { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Session[] sessions = new Session[Peers];
        ZeroPacket[] packets = new ZeroPacket[Peers];
        BenchmarkMessage message = new();
        PacketSender sender = new();
        byte[] content = new byte[MessageSize];
        for (int i = 0; i < Peers; i++)
        {
            IChannel channel = Substitute.For<IChannel>();
            IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
            context.Channel.Returns(channel);
            Session session = new(30303, new Node(TestItem.PublicKeyA, "127.0.0.1", 30303 + i), channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            session.Handshake(TestItem.PublicKeyA);
            session.Init(5, context, sender);
            session.AddProtocolHandler(new Handler(Protocol.P2P, 5, P2PSpaceSize));
            session.AddProtocolHandler(new Handler(Protocol.Eth, EthVersion, EthSpaceSize));
            sessions[i] = session;
            packets[i] = new Packet(null, AdaptiveId, content);
        }

        int iterations = BatchSize / Peers;
        _receive = peer =>
        {
            Session session = sessions[peer];
            ZeroPacket packet = packets[peer];
            for (int i = 0; i < iterations; i++)
            {
                packet.PacketType = AdaptiveId;
                session.ReceiveMessage(packet);
            }
        };
        _deliver = peer =>
        {
            Session session = sessions[peer];
            for (int i = 0; i < iterations; i++) session.DeliverMessage(message);
        };
    }

    [Benchmark(OperationsPerInvoke = BatchSize)]
    public void ReceiveMessage() => Parallel.For(0, Peers, _receive);

    [Benchmark(OperationsPerInvoke = BatchSize)]
    public void DeliverMessage() => Parallel.For(0, Peers, _deliver);

    private sealed class BenchmarkMessage : P2PMessage
    {
        public override int PacketType => Eth65MessageCode.NewPooledTransactionHashes;
        public override string Protocol => Nethermind.Network.Contract.P2P.Protocol.Eth;
    }

    private sealed class PacketSender : IPacketSender
    {
        public int Enqueue<T>(T message) where T : P2PMessage => MessageSize;
    }

    private sealed class Handler(string code, byte version, int spaceSize) : IZeroProtocolHandler
    {
        public string Name => code;
        public byte ProtocolVersion => version;
        public string ProtocolCode => code;
        public int MessageIdSpaceSize => spaceSize;
        public void Init() { }
        public void HandleMessage(Packet message) { }
        public void HandleMessage(ZeroPacket message) { }
        public void DisconnectProtocol(DisconnectReason disconnectReason, string details) { }
        public void RegisterWith(ISession session, IProtocolRegistrar registrar) { }
        public void Dispose() { }

        public event EventHandler<ProtocolInitializedEventArgs> ProtocolInitialized { add { } remove { } }
        public event EventHandler<ProtocolEventArgs> SubprotocolRequested { add { } remove { } }
    }
}
