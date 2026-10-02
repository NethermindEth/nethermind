// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DotNetty.Buffers;
using DotNetty.Transport.Channels.Embedded;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Lean;
using Nethermind.Network.Rlpx;
using Nethermind.Stats.Model;
using NSubstitute;

namespace Nethermind.Tools.LeanBench;

/// <summary>Production encrypted/compressed RLPx codecs across a localhost TCP socket pair.</summary>
public sealed class TcpRlpxLoopback : IDisposable
{
    private readonly EmbeddedChannel _outbound;
    private readonly EmbeddedChannel _inbound;
    private readonly FrameMacProcessor _outboundMac;
    private readonly FrameMacProcessor _inboundMac;
    private readonly Socket _sender;
    private readonly Socket _receiver;
    private const int ReadChunkBytes = 64 * 1024;
    private readonly LeanProofWrapperMessageSerializer _serializer = new();
    private TaskCompletionSource<byte[]>? _received;

    public TcpRlpxLoopback(Action<ZeroPacket>? receive = null)
    {
        byte[] aes = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        byte[] mac = Enumerable.Range(32, 32).Select(i => (byte)i).ToArray();
        KeccakHash initial = KeccakHash.Create();
        initial.Update("LeanBench loopback MAC seed"u8);
        EncryptionSecrets MakeSecrets() => new()
        {
            AesSecret = aes,
            MacSecret = mac,
            EgressMac = initial.Copy(),
            IngressMac = initial.Copy()
        };
        EncryptionSecrets a = MakeSecrets();
        EncryptionSecrets b = MakeSecrets();
        _outboundMac = new(TestItem.IgnoredPublicKey, a);
        _inboundMac = new(TestItem.IgnoredPublicKey, b);
        ZeroPacketSplitter splitter = new(new FrameCipher(aes), _outboundMac);
        splitter.EnableSnappy(LimboLogs.Instance);
        _outbound = new(splitter);
        ISession session = Substitute.For<ISession>();
        session.When(s => s.ReceiveMessage(Arg.Any<ZeroPacket>())).Do(call =>
        {
            ZeroPacket packet = call.Arg<ZeroPacket>();
            int readerIndex = packet.Content.ReaderIndex;
            byte[] wrapper = _serializer.Deserialize(packet.Content).Wrapper;
            packet.Content.SetReaderIndex(readerIndex);
            receive?.Invoke(packet);
            _received!.TrySetResult(wrapper);
        });
        session.When(s => s.InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>())).Do(call =>
            _received?.TrySetException(new InvalidOperationException(call.ArgAt<string>(1))));
        ZeroNettyP2PHandler handler = new(session, LimboLogs.Instance);
        handler.EnableSnappy();
        _inbound = new(new ZeroFrameDecoder(new FrameCipher(aes), _inboundMac),
            new ZeroFrameMerger(LimboLogs.Instance), handler);
        using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        _sender = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        _sender.Connect(listener.LocalEndPoint!);
        _receiver = listener.Accept();
        _receiver.NoDelay = true;
    }

    public async Task<TransportSample> TransferAsync(byte[] wrapper, CancellationToken cancellationToken)
    {
        _received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        long started = Stopwatch.GetTimestamp();
        IByteBuffer input = Unpooled.Buffer(wrapper.Length + 1);
        input.WriteByte(1);
        _serializer.Serialize(input, new LeanProofWrapperMessage(wrapper));
        _outbound.WriteOutbound(input);
        IByteBuffer wire = _outbound.ReadOutbound<IByteBuffer>();
        double encodeMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        int wireBytes = wire.ReadableBytes;
        long transferStarted = Stopwatch.GetTimestamp();
        try
        {
            Task receive = ReceiveAsync(cancellationToken);
            int offset = 0;
            while (offset < wireBytes)
                offset += await _sender.SendAsync(wire.Array.AsMemory(wire.ArrayOffset + wire.ReaderIndex + offset,
                    wireBytes - offset), SocketFlags.None, cancellationToken);
            byte[] decoded = await _received.Task.WaitAsync(cancellationToken);
            await receive;
            return new(decoded, wireBytes, encodeMs, Stopwatch.GetElapsedTime(transferStarted).TotalMilliseconds);
        }
        finally { wire.Release(); }
    }

    private async Task ReceiveAsync(CancellationToken cancellationToken)
    {
        while (!_received!.Task.IsCompleted)
        {
            IByteBuffer? input = Unpooled.Buffer(ReadChunkBytes);
            try
            {
                int read = await _receiver.ReceiveAsync(input.Array.AsMemory(input.ArrayOffset, ReadChunkBytes), SocketFlags.None, cancellationToken);
                if (read == 0) throw new EndOfStreamException("TCP peer closed");
                input.SetWriterIndex(read);
                _inbound.WriteInbound(input);
                input = null;
            }
            finally { input?.Release(); }
        }
    }

    public void Dispose()
    {
        _sender.Dispose();
        _receiver.Dispose();
        _outbound.FinishAndReleaseAll();
        _inbound.FinishAndReleaseAll();
        _outboundMac.Dispose();
        _inboundMac.Dispose();
    }
}

public sealed record TransportSample(byte[] Payload, int WireBytes, double EncodeMs, double TransferMs);
