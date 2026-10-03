// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
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
    private const int WrapperPacketType = 1;
    private const int TotalBytesOffset = Hash256.Size;
    private const int ChunkIndexOffset = TotalBytesOffset + sizeof(int);
    private const int ChunkCountOffset = ChunkIndexOffset + sizeof(int);
    private const int ChunkSizeOffset = ChunkCountOffset + sizeof(int);
    private readonly LeanProofWrapperMessageSerializer _serializer = new();
    private readonly LeanProofChunkMessageSerializer _chunks = new();
    private readonly bool _useChunks;
    private TaskCompletionSource<byte[]>? _received;
    private CancellationTokenSource? _transferStop;
    private int _disposed;

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
            if (packet.PacketType != WrapperPacketType) throw new InvalidDataException("Unexpected proof packet type");
            int readerIndex = packet.Content.ReaderIndex;
            if (_useChunks)
            {
                if (packet.Content.ReadableBytes < LeanProofChunkMessage.HeaderSize)
                    throw new InvalidDataException("Incomplete lean chunk header");
                int total = packet.Content.GetInt(readerIndex + TotalBytesOffset);
                int index = packet.Content.GetInt(readerIndex + ChunkIndexOffset);
                int count = packet.Content.GetInt(readerIndex + ChunkCountOffset);
                int size = packet.Content.GetInt(readerIndex + ChunkSizeOffset);
                if (!LeanProofChunkMessage.IsValidGeometry(total, index, count, size,
                        packet.Content.ReadableBytes - LeanProofChunkMessage.HeaderSize))
                    throw new InvalidDataException("Invalid lean chunk geometry");
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
        {
            _received?.TrySetException(new InvalidOperationException(call.ArgAt<string>(1)));
            _transferStop?.Cancel();
        });
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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (wrapper.Length is 0 or > LeanProofStore.MaxWrapperBytes) throw new ArgumentException("Invalid wrapper size", nameof(wrapper));
        using CancellationTokenSource transferStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _transferStop = transferStop;
        _received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? receive = null;
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
                    IByteBuffer? input = Unpooled.Buffer(data.Length + LeanProofChunkMessage.HeaderSize + 1);
                    try
                    {
                        LeanProofChunkMessage message = new(hash, wrapper.Length, index, count, chunkSize, data);
                        input.WriteByte(message.PacketType);
                        _chunks.Serialize(input, message);
                        IByteBuffer encoded = input;
                        input = null;
                        _outbound.WriteOutbound(encoded);
                        frames.Add(_outbound.ReadOutbound<IByteBuffer>());
                    }
                    finally { input?.Release(); }
                }
            }
            else
            {
                IByteBuffer? input = Unpooled.Buffer(wrapper.Length + 1);
                try
                {
                    LeanProofWrapperMessage message = new(wrapper);
                    input.WriteByte(message.PacketType);
                    _serializer.Serialize(input, message);
                    IByteBuffer encoded = input;
                    input = null;
                    _outbound.WriteOutbound(encoded);
                    frames.Add(_outbound.ReadOutbound<IByteBuffer>());
                }
                finally { input?.Release(); }
            }
            double encodeMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            int wireBytes = 0;
            long transferStarted = Stopwatch.GetTimestamp();
            receive = ReceiveAsync(transferStop);
            foreach (IByteBuffer wire in frames)
            {
                int length = wire.ReadableBytes;
                wireBytes += length;
                int offset = 0;
                while (offset < length)
                {
                    int sent = await _sender.SendAsync(wire.Array.AsMemory(wire.ArrayOffset + wire.ReaderIndex + offset,
                        length - offset), SocketFlags.None, transferStop.Token);
                    if (sent == 0) throw new EndOfStreamException("TCP peer closed");
                    offset += sent;
                }
            }
            byte[] decoded = await _received.Task.WaitAsync(transferStop.Token);
            await receive;
            return new(decoded, wireBytes, encodeMs, Stopwatch.GetElapsedTime(transferStarted).TotalMilliseconds);
        }
        catch
        {
            if (_received.Task.IsFaulted) await _received.Task;
            throw;
        }
        finally
        {
            transferStop.Cancel();
            try
            {
                if (receive is not null)
                {
                    try { await receive; }
                    catch (OperationCanceledException) when (transferStop.IsCancellationRequested) { }
                }
            }
            finally
            {
                _transferStop = null;
                foreach (IByteBuffer wire in frames) wire.Release();
            }
        }
    }

    private async Task ReceiveAsync(CancellationTokenSource transferStop)
    {
        try
        {
            while (!_received!.Task.IsCompleted)
            {
                IByteBuffer? input = Unpooled.Buffer(ReadChunkBytes);
                try
                {
                    int read = await _receiver.ReceiveAsync(input.Array.AsMemory(input.ArrayOffset, ReadChunkBytes), SocketFlags.None, transferStop.Token);
                    if (read == 0) throw new EndOfStreamException("TCP peer closed");
                    input.SetWriterIndex(read);
                    IByteBuffer received = input;
                    input = null;
                    _inbound.WriteInbound(received);
                }
                finally { input?.Release(); }
            }
        }
        catch (OperationCanceledException) when (transferStop.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            _received!.TrySetException(exception);
            transferStop.Cancel();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        List<Exception> failures = [];
        Cleanup(_sender.Dispose);
        Cleanup(_receiver.Dispose);
        Cleanup(() => _outbound.FinishAndReleaseAll());
        Cleanup(() => _inbound.FinishAndReleaseAll());
        Cleanup(_outboundMac.Dispose);
        Cleanup(_inboundMac.Dispose);
        if (failures.Count == 1) ExceptionDispatchInfo.Throw(failures[0]);
        if (failures.Count > 1) throw new AggregateException(failures);

        void Cleanup(Action release)
        {
            try { release(); }
            catch (Exception exception) { failures.Add(exception); }
        }
    }
}

public sealed record TransportSample(byte[] Payload, int WireBytes, double EncodeMs, double TransferMs);
