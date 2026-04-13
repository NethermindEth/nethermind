// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using Nethermind.Logging;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P;

public class PacketSender(IMessageSerializationService messageSerializationService, ILogManager logManager,
    TimeSpan sendLatency) : ChannelHandlerAdapter, IPacketSender
{
    private readonly IMessageSerializationService _messageSerializationService = messageSerializationService ?? throw new ArgumentNullException(nameof(messageSerializationService));
    private readonly ILogger _logger = logManager?.GetClassLogger<PacketSender>() ?? throw new ArgumentNullException(nameof(logManager));
    private readonly TimeSpan _sendLatency = sendLatency;
    private readonly CancellationTokenSource _cts = new();
    private IChannelHandlerContext _context;
    private Action<Task, object?> _delayThenWrite;
    private Action<Task, object?> _observeWriteCompletion;

    // Thread-safe: Netty guarantees single-threaded channel event delivery,
    // so ??= is never racing on these fields.
    private Action<Task, object?> DelayThenWriteAction => _delayThenWrite ??= DelayThenWrite;
    private Action<Task, object?> ObserveWriteCompletionAction => _observeWriteCompletion ??= ObserveWriteCompletion;

    public int Enqueue<T>(T message) where T : P2PMessage
    {
        if (!_context.Channel.IsWritable || !_context.Channel.Active)
        {
            return 0;
        }

        IByteBuffer buffer = _messageSerializationService.ZeroSerialize(message, allocator: _context.Allocator);
        int length = buffer.ReadableBytes;

        // Running in background
        SendBuffer(buffer);

        return length;
    }

    private void SendBuffer(IByteBuffer buffer)
    {
        try
        {
            if (_sendLatency != TimeSpan.Zero)
            {
                Task delayTask = Task.Delay(_sendLatency, _cts.Token);
                if (delayTask.IsCompletedSuccessfully)
                {
                    StartWrite(buffer);
                }
                else
                {
                    _ = delayTask.ContinueWith(
                        DelayThenWriteAction,
                        buffer,
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }

                return;
            }

            StartWrite(buffer);
        }
        catch (Exception exception)
        {
            HandleSendFailure(exception);
        }
    }

    private void DelayThenWrite(Task delayTask, object? state)
    {
        if (delayTask.IsFaulted)
        {
            HandleSendFailure(delayTask.Exception?.GetBaseException() ?? delayTask.Exception!);
            return;
        }

        if (delayTask.IsCanceled)
        {
            HandleSendFailure(new TaskCanceledException(delayTask));
            return;
        }

        StartWrite((IByteBuffer)state!);
    }

    private void StartWrite(IByteBuffer buffer)
    {
        try
        {
            Task writeTask = _context.WriteAndFlushAsync(buffer);
            if (writeTask.IsCompletedSuccessfully)
            {
                return;
            }

            if (writeTask.IsFaulted)
            {
                HandleSendFailure(writeTask.Exception?.GetBaseException() ?? writeTask.Exception!);
                return;
            }

            if (writeTask.IsCanceled)
            {
                HandleSendFailure(new TaskCanceledException(writeTask));
                return;
            }

            _ = writeTask.ContinueWith(
                ObserveWriteCompletionAction,
                null,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (Exception exception)
        {
            HandleSendFailure(exception);
        }
    }

    private void ObserveWriteCompletion(Task writeTask, object? _)
    {
        if (writeTask.IsFaulted)
        {
            HandleSendFailure(writeTask.Exception?.GetBaseException() ?? writeTask.Exception!);
        }
        else if (writeTask.IsCanceled)
        {
            HandleSendFailure(new TaskCanceledException(writeTask));
        }
    }

    private void HandleSendFailure(Exception exception)
    {
        if (_context.Channel is { Active: false })
        {
            if (_logger.IsTrace) LogTrace(exception);
        }
        else if (_logger.IsError)
        {
            LogError(exception);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void LogError(Exception exception) => _logger.Error("Channel is active", exception);

        [MethodImpl(MethodImplOptions.NoInlining)]
        void LogTrace(Exception exception) => _logger.Trace($"Channel is not active - {exception.Message}");
    }

    public override void HandlerAdded(IChannelHandlerContext context)
    {
        _context = context;
    }

    public override void HandlerRemoved(IChannelHandlerContext context)
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
