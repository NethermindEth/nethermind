// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Common.Utilities;
using DotNetty.Transport.Channels;
using Nethermind.Core.Buffers;
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

        PooledBuffer buffer = _messageSerializationService.ZeroSerialize(message);
        int length = buffer.Length;

        // Running in background
        SendBuffer(buffer);

        return length;
    }

    private void SendBuffer(PooledBuffer buffer)
    {
        try
        {
            if (_sendLatency != TimeSpan.Zero)
            {
                Task delayTask = Task.Delay(_sendLatency, _cts.Token);
                if (!delayTask.IsCompletedSuccessfully)
                {
                    _ = delayTask.ContinueWith(
                        DelayThenWriteAction,
                        buffer,
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            buffer.Dispose();
            HandleSendFailure(exception);
            return;
        }

        StartWrite(buffer);
    }

    private void DelayThenWrite(Task delayTask, object? state)
    {
        PooledBuffer buffer = (PooledBuffer)state!;
        if (delayTask.IsFaulted)
        {
            buffer.Dispose();
            HandleSendFailure(delayTask.Exception?.GetBaseException() ?? delayTask.Exception!);
            return;
        }

        if (delayTask.IsCanceled)
        {
            buffer.Dispose();
            HandleSendFailure(new TaskCanceledException(delayTask));
            return;
        }

        StartWrite(buffer);
    }

    private void StartWrite(PooledBuffer buffer)
    {
        // Zero-copy view over the pooled message for the DotNetty pipeline, which stays in
        // place for RLPx framing. The wrapper is released by the downstream encoder; the
        // pooled rental is returned below once the write completes.
        if (!MemoryMarshal.TryGetArray(buffer.ReadOnlyMemory, out ArraySegment<byte> segment) ||
            segment.Array is null)
        {
            buffer.Dispose();
            HandleSendFailure(new InvalidOperationException("Pooled message buffer is not array-backed."));
            return;
        }

        IByteBuffer wrapped = Unpooled.WrappedBuffer(segment.Array, segment.Offset, segment.Count);
        try
        {
            Task writeTask = _context.WriteAndFlushAsync(wrapped);
            if (writeTask.IsCompletedSuccessfully)
            {
                buffer.Dispose();
                return;
            }

            if (writeTask.IsFaulted)
            {
                buffer.Dispose();
                HandleSendFailure(writeTask.Exception?.GetBaseException() ?? writeTask.Exception!);
                return;
            }

            if (writeTask.IsCanceled)
            {
                buffer.Dispose();
                HandleSendFailure(new TaskCanceledException(writeTask));
                return;
            }

            _ = writeTask.ContinueWith(
                ObserveWriteCompletionAction,
                buffer,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (Exception exception)
        {
            // A synchronous throw means the pipeline never took ownership of the view.
            wrapped.SafeRelease();
            buffer.Dispose();
            HandleSendFailure(exception);
        }
    }

    private void ObserveWriteCompletion(Task writeTask, object? state)
    {
        ((PooledBuffer)state!).Dispose();
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

    public override void HandlerAdded(IChannelHandlerContext context) => _context = context;

    public override void HandlerRemoved(IChannelHandlerContext context)
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
