// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using DotNetty.Buffers;
using DotNetty.Transport.Channels.Embedded;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Rlpx;

namespace Nethermind.Network.Benchmarks;

/// <summary>
/// Measures the outbound encoder through the channel write path, including the output buffer allocation.
/// </summary>
[MemoryDiagnoser]
public class PacketSplitterWriteBenchmarks
{
    private const int MessagesPerOperation = 64;

    [Params(100, 1024, 16384, 262144)]
    public int PacketSize { get; set; }

    [Params(false, true)]
    public bool Snappy { get; set; }

    private byte[] _packet = null!;
    private EmbeddedChannel _channel = null!;
    private FrameMacProcessor _macProcessor = null!;

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(42);
        byte[] aes = new byte[32];
        byte[] macSecret = new byte[32];
        random.NextBytes(aes);
        random.NextBytes(macSecret);
        KeccakHash macTemplate = KeccakHash.Create();
        macTemplate.Update(aes);
        EncryptionSecrets secrets = new()
        {
            AesSecret = aes,
            MacSecret = macSecret,
            IngressMac = macTemplate.Copy(),
            EgressMac = macTemplate.Copy()
        };

        // Half random bytes and half zeros, so Snappy compresses part of the payload.
        _packet = new byte[PacketSize];
        random.NextBytes(_packet.AsSpan(0, PacketSize / 2));
        _packet[0] = 0x10;

        _macProcessor = new FrameMacProcessor(secrets);
        ZeroPacketSplitter splitter = new(new FrameCipher(aes), _macProcessor);
        if (Snappy) splitter.EnableSnappy(NullLogManager.Instance);
        _channel = new EmbeddedChannel(splitter);
        _channel.Configuration.Allocator = PooledByteBufferAllocator.Default;
    }

    [Benchmark(OperationsPerInvoke = MessagesPerOperation)]
    public int Write()
    {
        int written = 0;
        for (int i = 0; i < MessagesPerOperation; i++)
        {
            IByteBuffer input = PooledByteBufferAllocator.Default.Buffer(_packet.Length);
            input.WriteBytes(_packet);
            _channel.WriteOutbound(input);
            IByteBuffer output = _channel.ReadOutbound<IByteBuffer>();
            written += output.ReadableBytes;
            output.Release();
        }

        return written;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _channel.FinishAndReleaseAll();
        _macProcessor.Dispose();
    }
}
