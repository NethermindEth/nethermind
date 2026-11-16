// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using DotNetty.Buffers;
using DotNetty.Common.Internal.Logging;
using DotNetty.Handlers.Logging;
using DotNetty.Transport.Channels.Embedded;
using Nethermind.Logging;
using Nethermind.Logging.Microsoft;

namespace Nethermind.Network.Benchmarks;

/// <summary>
/// Measures the per-message cost of a disabled TRACE <see cref="LoggingHandler"/> at the head of an RLPx pipeline.
/// </summary>
[MemoryDiagnoser]
public class LoggingHandlerPipelineBenchmarks
{
    private const int MessagesPerOperation = 256;

    [Params(false, true)]
    public bool WithLoggingHandler { get; set; }

    private EmbeddedChannel _channel = null!;
    private IByteBuffer _message = null!;

    [GlobalSetup]
    public void Setup()
    {
        InternalLoggerFactory.DefaultFactory = new NethermindLoggerFactory(NullLogManager.Instance, lowerLogLevel: true);
        _channel = WithLoggingHandler ? new EmbeddedChannel(new LoggingHandler("OUT", DotNetty.Handlers.Logging.LogLevel.TRACE)) : new EmbeddedChannel();
        _message = Unpooled.Buffer(64).WriteZero(64);
    }

    [Benchmark(OperationsPerInvoke = MessagesPerOperation)]
    public int WriteAndRead()
    {
        int count = 0;
        for (int i = 0; i < MessagesPerOperation; i++)
        {
            _channel.WriteOutbound(_message);
            if (_channel.ReadOutbound<IByteBuffer>() is not null) count++;
            _channel.WriteInbound(_message);
            if (_channel.ReadInbound<IByteBuffer>() is not null) count++;
        }

        return count;
    }

    [GlobalCleanup]
    public void Cleanup() => _channel.FinishAndReleaseAll();
}
