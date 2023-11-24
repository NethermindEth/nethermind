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
            TimeSpan lastExecutionDuration = _throttleTime;
            int lastSize = 0;

            while (await _channel.Reader.WaitToReadAsync())
            {
                while (_channel.Reader.TryRead(out IByteBuffer buffer))
                {
                    try
                    {
                        TimeSpan delay = _throttleTime - lastExecutionDuration;
                        int sizeBudget = _byteLimit - lastSize;
                        if (sizeBudget <= 0 && delay > TimeSpan.Zero)
                        {
                            await Task.Delay(delay);
                        }
                        Stopwatch watch = Stopwatch.StartNew();
                        await _context!.WriteAndFlushAsync(buffer);
                        lastExecutionDuration = TimeSpan.FromMilliseconds(watch.ElapsedMilliseconds);
                        lastSize = buffer.ReadableBytes;
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
