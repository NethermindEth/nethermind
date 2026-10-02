// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Embedded;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Lean;
using Nethermind.Network.Rlpx;
using Nethermind.Stats.Model;
using NSubstitute;

namespace Nethermind.Tools.LeanBench;

/// <summary>Production PacketSender and encrypted RLPx codecs with a paced localhost TCP sink.</summary>
internal sealed class MixedTrafficTransport : IAsyncDisposable
{
    private const int BulkCode = 32;
    private const int HeaderCode = 19;
    private const int HeaderResponseCode = 20;
    private const int PingCode = 2;
    private readonly Socket _sender;
    private readonly Socket _receiver;
    private readonly EmbeddedChannel _outbound;
    private readonly EmbeddedChannel _inbound;
    private readonly FrameMacProcessor _outboundMac;
    private readonly FrameMacProcessor _inboundMac;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _reading;
    private readonly PacedSocketWriter _writer;
    private readonly PacketSender _packets;
    private readonly GetBlockHeadersMessageSerializer _headers = new();
    private readonly BlockHeadersMessageSerializer _headerResponses = new();
    private readonly LeanProofWrapperMessageSerializer _whole = new();
    private readonly LeanProofChunkMessageSerializer _chunks = new();
    private readonly ConcurrentDictionary<ulong, long> _headerStarts = [];
    private readonly ConcurrentQueue<long> _pingStarts = [];
    private readonly ConcurrentQueue<ControlSample> _controls = [];
    private readonly int _chunkSize;
    private TaskCompletionSource<byte[]>? _delivered;
    private LeanChunkReassembler? _reassembler;
    private ValueHash256 _objectHash;
    public long WireBytes => _writer.WireBytes;
    public int WriteDrops => _writer.Drops;
    public ControlSample[] Controls => _controls.ToArray();

    public MixedTrafficTransport(int chunkSize, double wireMbps)
    {
        _chunkSize = chunkSize;
        using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        _sender = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        _sender.Connect(listener.LocalEndPoint!);
        _receiver = listener.Accept();
        _receiver.NoDelay = true;
        byte[] aes = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        byte[] mac = Enumerable.Range(32, 32).Select(i => (byte)i).ToArray();
        KeccakHash initial = KeccakHash.Create();
        initial.Update("LeanBench mixed traffic MAC seed"u8);
        EncryptionSecrets Secrets() => new() { AesSecret = aes, MacSecret = mac, EgressMac = initial.Copy(), IngressMac = initial.Copy() };
        _outboundMac = new(TestItem.IgnoredPublicKey, Secrets());
        _inboundMac = new(TestItem.IgnoredPublicKey, Secrets());
        ZeroPacketSplitter splitter = new(new FrameCipher(aes), _outboundMac);
        splitter.EnableSnappy(LimboLogs.Instance);
        MessageSerializationService serialization = new(
            SerializerInfo.Create(_whole), SerializerInfo.Create(_chunks),
            SerializerInfo.Create(_headers), SerializerInfo.Create(_headerResponses), SerializerInfo.Create(new PingMessageSerializer()));
        _packets = new(serialization, LimboLogs.Instance, TimeSpan.Zero);
        _writer = new(_sender, wireMbps, _stop.Token);
        _outbound = new(_writer, splitter, _packets);
        ISession session = Substitute.For<ISession>();
        session.When(s => s.ReceiveMessage(Arg.Any<ZeroPacket>())).Do(call => Receive(call.Arg<ZeroPacket>()));
        session.When(s => s.InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>())).Do(call =>
            _delivered?.TrySetException(new InvalidOperationException(call.ArgAt<string>(1))));
        ZeroNettyP2PHandler handler = new(session, LimboLogs.Instance);
        handler.EnableSnappy();
        _inbound = new(new ZeroFrameDecoder(new FrameCipher(aes), _inboundMac), new ZeroFrameMerger(LimboLogs.Instance), handler);
        _reading = ReadAsync();
    }

    public async Task<byte[]> TransferAsync(byte[] payload, CancellationToken token)
    {
        _delivered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _objectHash = ValueKeccak.Compute(payload);
        _reassembler?.Dispose();
        _reassembler = _chunkSize == 0 ? null : new(new LeanReassemblyBudget());
        if (_chunkSize == 0)
        {
            LeanProofWrapperMessage message = new(payload) { AdaptivePacketType = BulkCode };
            if (await _packets.EnqueueAsync(message, token) == 0) throw new IOException("Whole-wrapper write declined");
        }
        else
        {
            int count = (payload.Length + _chunkSize - 1) / _chunkSize;
            for (int index = 0; index < count; index++)
            {
                int offset = index * _chunkSize;
                LeanProofChunkMessage message = new(_objectHash, payload.Length, index, count, _chunkSize,
                    payload.AsMemory(offset, Math.Min(_chunkSize, payload.Length - offset)))
                { AdaptivePacketType = BulkCode };
                if (await _packets.EnqueueAsync(message, token) == 0) throw new IOException("Chunk write declined");
            }
        }
        byte[] delivered = await _delivered.Task.WaitAsync(token);
        if (!delivered.AsSpan().SequenceEqual(payload)) throw new InvalidDataException("Object changed in transport");
        return delivered;
    }

    public bool Probe(ulong sequence, long scheduled, int kind)
    {
        if (kind == 0)
        {
            _pingStarts.Enqueue(scheduled);
            PingMessage.Instance.AdaptivePacketType = PingCode;
            return _packets.Enqueue(PingMessage.Instance) != 0;
        }
        _headerStarts[sequence] = scheduled;
        if (kind == 1)
        {
            GetBlockHeadersMessage message = new() { AdaptivePacketType = HeaderCode, StartBlockNumber = sequence, MaxHeaders = 1 };
            return _packets.Enqueue(message) != 0;
        }
        using BlockHeadersMessage response = new(new ArrayPoolList<BlockHeader>(1, 1)
        {
            [0] = Build.A.BlockHeader.WithNumber(sequence).TestObject
        })
        { AdaptivePacketType = HeaderResponseCode };
        return _packets.Enqueue(response) != 0;
    }

    private void Receive(ZeroPacket packet)
    {
        if (packet.PacketType == HeaderCode)
        {
            GetBlockHeadersMessage message = _headers.Deserialize(packet.Content);
            if (!_headerStarts.TryRemove(message.StartBlockNumber, out long scheduled)) throw new InvalidDataException("Unexpected header probe");
            _controls.Enqueue(new("eth-getBlockHeaders", scheduled, Stopwatch.GetElapsedTime(scheduled).TotalMilliseconds));
        }
        else if (packet.PacketType == HeaderResponseCode)
        {
            using BlockHeadersMessage response = _headerResponses.Deserialize(packet.Content);
            ulong sequence = response.BlockHeaders![0].Number;
            if (!_headerStarts.TryRemove(sequence, out long scheduled)) throw new InvalidDataException("Unexpected header response probe");
            _controls.Enqueue(new("eth-blockHeaders", scheduled, Stopwatch.GetElapsedTime(scheduled).TotalMilliseconds));
        }
        else if (packet.PacketType == PingCode)
        {
            if (!_pingStarts.TryDequeue(out long scheduled)) throw new InvalidDataException("Unexpected ping probe");
            _controls.Enqueue(new("p2p-ping", scheduled, Stopwatch.GetElapsedTime(scheduled).TotalMilliseconds));
        }
        else if (packet.PacketType == BulkCode && _chunkSize == 0) _delivered!.TrySetResult(_whole.Deserialize(packet.Content).Wrapper);
        else if (packet.PacketType == BulkCode)
        {
            LeanProofChunkMessage message = _chunks.Deserialize(packet.Content);
            if (message.WrapperHash != _objectHash) throw new InvalidDataException("Unexpected chunk hash");
            using LeanProofWrapperMessage? completed = _reassembler!.Add(message);
            if (completed is not null) _delivered!.TrySetResult(completed.Wrapper);
        }
        else throw new InvalidDataException("Unexpected packet type");
    }

    private async Task ReadAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                IByteBuffer? buffer = Unpooled.Buffer(64 * 1024);
                try
                {
                    int read = await _receiver.ReceiveAsync(buffer.Array.AsMemory(buffer.ArrayOffset, buffer.Capacity), SocketFlags.None, _stop.Token);
                    if (read == 0) throw new EndOfStreamException();
                    buffer.SetWriterIndex(read);
                    _inbound.WriteInbound(buffer);
                    buffer = null;
                }
                finally { buffer?.Release(); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception) { _delivered?.TrySetException(exception); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _reading;
        await _writer.Completion;
        _reassembler?.Dispose();
        _outbound.FinishAndReleaseAll();
        _inbound.FinishAndReleaseAll();
        _outboundMac.Dispose();
        _inboundMac.Dispose();
        _sender.Dispose();
        _receiver.Dispose();
        _stop.Dispose();
    }

    private sealed class PacedSocketWriter : ChannelHandlerAdapter
    {
        private sealed record Write(IByteBuffer Buffer, TaskCompletionSource Completion);
        private readonly Channel<Write> _queue = Channel.CreateBounded<Write>(256);
        private readonly Socket _socket;
        private readonly double _bytesPerSecond;
        private readonly CancellationToken _token;
        private long _bytes;
        private int _drops;
        public long WireBytes => Interlocked.Read(ref _bytes);
        public int Drops => Volatile.Read(ref _drops);
        public Task Completion { get; }
        public PacedSocketWriter(Socket socket, double wireMbps, CancellationToken token)
        {
            _socket = socket;
            _bytesPerSecond = wireMbps * 1_000_000 / 8;
            _token = token;
            Completion = DrainAsync();
        }
        public override Task WriteAsync(IChannelHandlerContext context, object message)
        {
            Write write = new((IByteBuffer)message, new(TaskCreationOptions.RunContinuationsAsynchronously));
            if (!_queue.Writer.TryWrite(write))
            {
                Interlocked.Increment(ref _drops);
                write.Buffer.Release();
                write.Completion.SetException(new IOException("Paced TCP queue full"));
            }
            return write.Completion.Task;
        }
        private async Task DrainAsync()
        {
            double due = Stopwatch.GetTimestamp();
            try
            {
                await foreach (Write write in _queue.Reader.ReadAllAsync(_token))
                {
                    try
                    {
                        due = Math.Max(due, Stopwatch.GetTimestamp());
                        while (write.Buffer.IsReadable())
                        {
                            int length = Math.Min(4096, write.Buffer.ReadableBytes);
                            int sent = await _socket.SendAsync(write.Buffer.Array.AsMemory(write.Buffer.ArrayOffset + write.Buffer.ReaderIndex, length), SocketFlags.None, _token);
                            if (sent == 0) throw new IOException("TCP write made no progress");
                            write.Buffer.SkipBytes(sent);
                            Interlocked.Add(ref _bytes, sent);
                            due += sent / _bytesPerSecond * Stopwatch.Frequency;
                            double delay = (due - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
                            if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), _token);
                        }
                        write.Completion.TrySetResult();
                    }
                    catch (Exception exception) { write.Completion.TrySetException(exception); throw; }
                    finally { write.Buffer.Release(); }
                }
            }
            catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
            finally
            {
                _queue.Writer.TryComplete();
                while (_queue.Reader.TryRead(out Write? write))
                {
                    write.Buffer.Release();
                    write.Completion.TrySetCanceled();
                }
            }
        }
    }
}

internal sealed record ControlSample(string Kind, long ScheduledTimestamp, double LatencyMs);
