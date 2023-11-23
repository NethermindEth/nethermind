// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.Network.P2P.ProtocolHandlers;

public class ThrottledActionQueue : IDisposable
{
    private readonly Channel<Func<Task>> _channel;
    private readonly ILogger _logger;
    private readonly TimeSpan _throttleTime;

    private Task? _processor;

    public ThrottledActionQueue(TimeSpan throttleTime, ILogger logger)
    {
        _throttleTime = throttleTime;
        _logger = logger;
        _channel = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });
    }

    public void Init()
    {
        _processor = Task.Run(async () =>
        {
            TimeSpan lastExecutionDuration = _throttleTime;

            while (await _channel.Reader.WaitToReadAsync())
            {
                while (_channel.Reader.TryRead(out Func<Task> action))
                {
                    try
                    {
                        await Task.Delay(_throttleTime - lastExecutionDuration);
                        Stopwatch watch = Stopwatch.StartNew();
                        await action();
                        lastExecutionDuration = TimeSpan.FromMilliseconds(watch.ElapsedMilliseconds);
                    }
                    catch (Exception e)
                    {
                        if (_logger.IsError) _logger.Error("TODO", e);
                    }
                }
            }
        });
    }

    public bool Enqueue(Func<Task> task) => _channel.Writer.TryWrite(task);

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _processor?.Wait(-1);
        Interlocked.Exchange(ref _processor, null);
    }
}
