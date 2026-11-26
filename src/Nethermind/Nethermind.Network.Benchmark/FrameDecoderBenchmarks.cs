// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using DotNetty.Buffers;
using DotNetty.Transport.Channels.Embedded;
using Nethermind.Core.Crypto;
using Nethermind.Network.Rlpx;

namespace Nethermind.Network.Benchmarks;

[MemoryDiagnoser]
public class FrameDecoderBenchmarks
{
    private const int StreamBytes = 4 * 1024 * 1024;

    [Params(1024, 65536, 1048576)]
    public int PacketSize { get; set; }

    [Params(16384, 65536)]
    public int ReadSize { get; set; }

    [Params(false, true)]
    public bool Framed { get; set; }

    private byte[] _aes = null!;
    private byte[] _macSecret = null!;
    private KeccakHash _macTemplate = null!;
    private byte[] _wire = null!;
    private int _packets;

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(42);
        _aes = new byte[32];
        _macSecret = new byte[32];
        random.NextBytes(_aes);
        random.NextBytes(_macSecret);
        _macTemplate = KeccakHash.Create();
        _macTemplate.Update(_aes);
        _packets = Math.Max(1, StreamBytes / PacketSize);

        using FrameMacProcessor mac = new(Secrets());
        ZeroPacketSplitter splitter = new(new FrameCipher(_aes), mac);
        if (!Framed)
        {
            splitter.DisableFraming();
        }

        EmbeddedChannel encoder = new(splitter);
        byte[] packet = new byte[PacketSize];
        random.NextBytes(packet);
        packet[0] = 2;
        List<byte> wire = [];
        for (int i = 0; i < _packets; i++)
        {
            encoder.WriteOutbound(Unpooled.WrappedBuffer(packet));
            IByteBuffer output = encoder.ReadOutbound<IByteBuffer>();
            byte[] bytes = new byte[output.ReadableBytes];
            output.ReadBytes(bytes);
            output.Release();
            wire.AddRange(bytes);
        }

        _wire = wire.ToArray();
        int decoded = Decode();
        int expected = Framed ? _packets * ((PacketSize - 1) / Frame.DefaultMaxFrameSize + 1) : _packets;
        if (decoded != expected)
        {
            throw new InvalidOperationException($"Decoded {decoded} frames, expected {expected}");
        }
    }

    private EncryptionSecrets Secrets() => new()
    {
        AesSecret = _aes,
        MacSecret = _macSecret,
        IngressMac = _macTemplate.Copy(),
        EgressMac = _macTemplate.Copy()
    };

    [Benchmark]
    public int Decode()
    {
        using FrameMacProcessor mac = new(Secrets());
        EmbeddedChannel channel = new(new ZeroFrameDecoder(new FrameCipher(_aes), mac));
        channel.Configuration.Allocator = PooledByteBufferAllocator.Default;
        int frames = 0;
        byte[] wire = _wire;
        for (int offset = 0; offset < wire.Length; offset += ReadSize)
        {
            int length = Math.Min(ReadSize, wire.Length - offset);
            channel.WriteInbound(PooledByteBufferAllocator.Default.Buffer(length).WriteBytes(wire, offset, length));
            while (channel.ReadInbound<IByteBuffer>() is { } frame)
            {
                frame.Release();
                frames++;
            }
        }
        channel.FinishAndReleaseAll();
        return frames;
    }
}
