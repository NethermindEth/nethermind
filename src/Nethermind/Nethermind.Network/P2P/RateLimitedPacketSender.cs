// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using Nethermind.Logging;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P;

public class RateLimitedPacketSender : ChannelHandlerAdapter, IPacketSender, IDisposable
{
    private readonly int _byteLimit;
    private readonly TimeSpan _throttleTime;
    private readonly IMessageSerializationService _messageSerializationService;
    private readonly ILogger _logger;
    private readonly Channel<IByteBuffer> _channel;

    private IChannelHandlerContext? _context;
    private Task? _processor;

    public RateLimitedPacketSender(int byteLimit, TimeSpan throttleTime, IMessageSerializationService messageSerializationService, ILogManager logManager)
    {
        _byteLimit = byteLimit;
        _throttleTime = throttleTime;
        _messageSerializationService = messageSerializationService;
        _logger = logManager.GetClassLogger<RateLimitedPacketSender>();
        _channel = Channel.CreateUnbounded<IByteBuffer>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });
    }

    public void Init()
    {
        _processor = Task.Run(async () =>
        {
            TimeSpan durationSoFar = TimeSpan.Zero;
            int bytesSentSoFar = 0;

            while (await _channel.Reader.WaitToReadAsync())
            {
                while (_channel.Reader.TryRead(out IByteBuffer buffer))
                {
                    try
                    {
                        int bytesToSend = buffer.ReadableBytes;
                        if (bytesSentSoFar + bytesToSend > _byteLimit)
                        {
                            if (_throttleTime - durationSoFar > TimeSpan.Zero)
                            {
                                await Task.Delay(_throttleTime - durationSoFar);
                            }
                            bytesSentSoFar = 0;
                            durationSoFar = TimeSpan.Zero;
                        }
                        Stopwatch stopwatch = Stopwatch.StartNew();
                        await _context!.WriteAndFlushAsync(buffer);
                        durationSoFar += stopwatch.Elapsed;
                        bytesSentSoFar += bytesToSend;
                    }
                    catch (Exception e)
                    {
                        if (_logger.IsError) _logger.Error($"Unhandled exception on ${nameof(RateLimitedPacketSender)} processor loop", e);
                    }
                }
            }
        });
    }

    public int Enqueue<T>(T message) where T : P2PMessage
    {
        if (_context?.Channel is { Active: false })
        {
            return 0;
        }

        IByteBuffer buffer = _messageSerializationService.ZeroSerialize(message);
        _channel.Writer.TryWrite(buffer);
        return buffer.ReadableBytes;
    }

    public override void HandlerAdded(IChannelHandlerContext context)
    {
        _context = context;
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _processor?.Wait(-1);
        Interlocked.Exchange(ref _processor, null);
    }
}
