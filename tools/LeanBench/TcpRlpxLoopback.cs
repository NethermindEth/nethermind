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
    private readonly LeanProofChunkMessageSerializer _chunks = new();
    private readonly bool _useChunks;
    private TaskCompletionSource<byte[]>? _received;

    public TcpRlpxLoopback(Action<ZeroPacket>? receive = null)
    {
        _useChunks = receive is not null;
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
            if (_useChunks)
            {
                int index = packet.Content.GetInt(readerIndex + 36);
                int count = packet.Content.GetInt(readerIndex + 40);
                receive!(packet);
                // The protocol queue owns reassembly and integrity checks; this result marks wire delivery.
                if (index == count - 1) _received!.TrySetResult([]);
                return;
            }
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
        if (wrapper.Length is 0 or > LeanProofStore.MaxWrapperBytes) throw new ArgumentException("Invalid wrapper size", nameof(wrapper));
        _received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        long started = Stopwatch.GetTimestamp();
        List<IByteBuffer> frames = [];
        try
        {
            if (_useChunks)
            {
                ValueHash256 hash = ValueKeccak.Compute(wrapper);
                const int chunkSize = LeanProofChunkMessage.DefaultChunkSize;
                int count = (wrapper.Length + chunkSize - 1) / chunkSize;
                for (int index = 0; index < count; index++)
                {
                    int offset = index * chunkSize;
                    ReadOnlyMemory<byte> data = wrapper.AsMemory(offset, Math.Min(chunkSize, wrapper.Length - offset));
                    IByteBuffer input = Unpooled.Buffer(data.Length + LeanProofChunkMessage.HeaderSize + 1);
                    input.WriteByte(1);
                    _chunks.Serialize(input, new(hash, wrapper.Length, index, count, chunkSize, data));
                    _outbound.WriteOutbound(input);
                    frames.Add(_outbound.ReadOutbound<IByteBuffer>());
                }
            }
            else
            {
                IByteBuffer input = Unpooled.Buffer(wrapper.Length + 1);
                input.WriteByte(1);
                _serializer.Serialize(input, new LeanProofWrapperMessage(wrapper));
                _outbound.WriteOutbound(input);
                frames.Add(_outbound.ReadOutbound<IByteBuffer>());
            }
            double encodeMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            int wireBytes = 0;
            long transferStarted = Stopwatch.GetTimestamp();
            Task receive = ReceiveAsync(cancellationToken);
            foreach (IByteBuffer wire in frames)
            {
                int length = wire.ReadableBytes;
                wireBytes += length;
                int offset = 0;
                while (offset < length)
                {
                    int sent = await _sender.SendAsync(wire.Array.AsMemory(wire.ArrayOffset + wire.ReaderIndex + offset,
                        length - offset), SocketFlags.None, cancellationToken);
                    if (sent == 0) throw new EndOfStreamException("TCP peer closed");
                    offset += sent;
                }
            }
            byte[] decoded = await _received.Task.WaitAsync(cancellationToken);
            await receive;
            return new(decoded, wireBytes, encodeMs, Stopwatch.GetElapsedTime(transferStarted).TotalMilliseconds);
        }
        finally { foreach (IByteBuffer wire in frames) wire.Release(); }
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
