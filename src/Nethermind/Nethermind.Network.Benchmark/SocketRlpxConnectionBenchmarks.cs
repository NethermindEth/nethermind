// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.Network.Rlpx.Sockets;
using Nethermind.Network.Test;
using Nethermind.Network.Test.Rlpx.Sockets;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Benchmarks;

/// <summary>
/// Measures a socket transport connection on an in-memory stream: frames received into packets for the session, and a
/// burst of packets sent in batches, with the stream whole or split into small reads and sends.
/// </summary>
/// <remarks>
/// The peer encodes into a reused buffer and drains what it receives without decoding, so the allocations are the
/// connection's. The time includes the peer encrypting what the connection receives.
/// </remarks>
[MemoryDiagnoser]
public class SocketRlpxConnectionBenchmarks
{
    private const int PacketsPerOperation = 64;
    private const int FragmentSize = 1000;

    [Params(100, 16384)]
    public int PacketSize { get; set; }

    [Params(false, true)]
    public bool Fragmented { get; set; }

    private InMemoryRlpxSocket _socket = null!;
    private ScriptedRlpxPeer _peer = null!;
    private RlpxProcessingThreads _threads = null!;
    private SocketRlpxConnection _connection = null!;
    private CountingSession _session = null!;
    private byte[] _packet = null!;
    private byte[] _frames = null!;
    private byte[] _drain = null!;
    private int _encodedLength;

    [GlobalSetup]
    public void Setup() => SetUpAsync().GetAwaiter().GetResult();

    private async Task SetUpAsync()
    {
        (EncryptionSecrets initiator, EncryptionSecrets recipient) = NetTestVectors.GetSecretsPair();
        (_socket, InMemoryRlpxSocket peerSocket) = InMemoryRlpxSocket.CreatePair();
        _peer = new ScriptedRlpxPeer(peerSocket, initiator);
        _threads = new RlpxProcessingThreads(1, LimboLogs.Instance.GetClassLogger<SocketRlpxConnectionBenchmarks>());
        _connection = new SocketRlpxConnection(
            _socket,
            HandshakeRole.Recipient,
            new FakeHandshakeService(recipient, TestItem.PublicKeyA),
            new MessageSerializationService(SerializerInfo.Create(new PayloadSerializer())),
            _threads.Next(),
            LimboLogs.Instance,
            TimeSpan.Zero,
            ZeroFrameDecoder.DefaultMaxInboundFrameSize);
        _session = new CountingSession();
        _connection.Start(_session);
        await _peer.SendAsync(FakeHandshakeService.Message);
        await _peer.ReceiveHandshakeAsync(CancellationToken.None);
        await _session.Initialized.Task;

        _packet = new byte[1 + PacketSize];
        _packet[0] = 0x10;
        new Random(42).NextBytes(_packet.AsSpan(1));
        _frames = new byte[_peer.GetMaxEncodedLength(_packet.Length, FrameEncoder.EncodingMode.Framed)];

        // A packet's encoded length doesn't depend on the cipher state, so one sample gives it for every packet.
        long received = _peer.Socket.Received;
        _connection.Enqueue(new PayloadMessage(_packet.AsMemory(1)) { AdaptivePacketType = 0x10 });
        await _peer.ReceivePacketAsync(CancellationToken.None);
        _encodedLength = (int)(_peer.Socket.Received - received);
        _drain = new byte[_encodedLength * PacketsPerOperation];
        _socket.MaxReceiveLength = _socket.MaxSendLength = Fragmented ? FragmentSize : int.MaxValue;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _connection.DisconnectAsync().GetAwaiter().GetResult();
        _connection.Completion.GetAwaiter().GetResult();
        _threads.StopAsync().GetAwaiter().GetResult();
        _peer.Socket.Close();
    }

    [Benchmark(OperationsPerInvoke = PacketsPerOperation)]
    public async Task Receive()
    {
        Task received = _session.WaitForPacketsAsync(PacketsPerOperation);
        for (int i = 0; i < PacketsPerOperation; i++)
        {
            int length = _peer.EncodePacket(_packet, _frames, FrameEncoder.EncodingMode.Framed);
            await _peer.Socket.SendAllAsync(_frames.AsMemory(0, length));
        }

        await received;
    }

    [Benchmark(OperationsPerInvoke = PacketsPerOperation)]
    public async Task SendBurst()
    {
        for (int i = 0; i < PacketsPerOperation; i++)
        {
            _connection.Enqueue(new PayloadMessage(_packet.AsMemory(1)) { AdaptivePacketType = 0x10 });
        }

        int received = 0;
        while (received < _drain.Length)
        {
            received += await _peer.Socket.ReceiveAsync(_drain.AsMemory(received), CancellationToken.None);
        }
    }

    private sealed class PayloadMessage(ReadOnlyMemory<byte> payload) : P2PMessage
    {
        public override int PacketType => 0x10;

        public override string Protocol => "benchmark";

        public ReadOnlyMemory<byte> Payload => payload;
    }

    private sealed class PayloadSerializer : IMessageSerializer<PayloadMessage>
    {
        public int GetLength(PayloadMessage message) => message.Payload.Length;

        public int Serialize(Span<byte> destination, PayloadMessage message)
        {
            message.Payload.Span.CopyTo(destination);
            return message.Payload.Length;
        }

        public PayloadMessage Deserialize(ReadOnlySpan<byte> data) => throw new NotSupportedException();
    }

    /// <summary>A session that counts the packets it receives, and allocates nothing doing so.</summary>
    private sealed class CountingSession : ISession
    {
        private int _received;
        private int _target;
        private TaskCompletionSource _reached;

        public TaskCompletionSource Initialized { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForPacketsAsync(int count)
        {
            TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _received, 0);
            _target = count;
            Volatile.Write(ref _reached, reached);
            return reached.Task;
        }

        public void ReceiveMessage(ZeroPacket zeroPacket)
        {
            if (Interlocked.Increment(ref _received) == _target)
            {
                Volatile.Read(ref _reached)?.TrySetResult();
            }
        }

        public void Init(byte p2PVersion, IPacketSender packetSender) => Initialized.TrySetResult();

        public void Handshake(PublicKey handshakeRemoteNodeId)
        {
        }

        public void InitiateDisconnect(DisconnectReason disconnectReason, string details) =>
            throw new InvalidOperationException($"Disconnect: {disconnectReason} {details}");

        public bool IsClosing => false;
        public PublicKey RemoteNodeId => TestItem.PublicKeyA;
        public string RemoteHost { get; set; } = "127.0.0.1";
        public int RemotePort { get; set; } = 30303;
        public Node Node { get; } = new(TestItem.PublicKeyA, "127.0.0.1", 30303);

        public byte P2PVersion => 5;
        public SessionState State => SessionState.Initialized;
        public SessionState BestStateReached => SessionState.Initialized;
        public bool IsChannelClosed => false;
        public bool IsOutboundBufferFull => false;
        public PublicKey ObsoleteRemoteNodeId => throw new NotSupportedException();
        public int LocalPort => 30303;
        public bool IsNetworkIdMatched { get; set; }
        public ConnectionDirection Direction => ConnectionDirection.In;
        public Guid SessionId { get; } = Guid.NewGuid();
        public DateTime LastPingUtc { get; set; }
        public DateTime LastPongUtc { get; set; }
        public IPingSender PingSender { get; set; } = null!;
        public int DeliverMessage<T>(T message) where T : P2PMessage => throw new NotSupportedException();
        public void EnableSnappy() => throw new NotSupportedException();
        public void AddSupportedCapability(Capability capability) => throw new NotSupportedException();
        public bool HasAvailableCapability(Capability capability) => throw new NotSupportedException();
        public bool HasAgreedCapability(Capability capability) => throw new NotSupportedException();
        public void AddProtocolHandler(IProtocolHandler handler) => throw new NotSupportedException();
        public bool TryGetProtocolHandler(string protocolCode, out IProtocolHandler handler) => throw new NotSupportedException();
        public void MarkDisconnected(DisconnectReason disconnectReason, DisconnectType disconnectType, string details) => throw new NotSupportedException();
        public void Dispose() { }

        public event EventHandler<DisconnectEventArgs> Disconnecting { add { } remove { } }
        public event EventHandler<DisconnectEventArgs> Disconnected { add { } remove { } }
        event EventHandler<EventArgs> ISession.Initialized { add { } remove { } }
        public event EventHandler<EventArgs> HandshakeComplete { add { } remove { } }
        public event EventHandler<PeerEventArgs> MsgReceived { add { } remove { } }
        public event EventHandler<PeerEventArgs> MsgDelivered { add { } remove { } }
    }
}
