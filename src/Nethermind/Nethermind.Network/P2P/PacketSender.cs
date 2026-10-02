// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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
    private const int MaxDeferredBytes = 12 * 1024 * 1024;
    private const int MaxDeferredMessages = 64;
    private readonly Lock _deferredLock = new();
    private readonly Queue<IByteBuffer> _deferred = [];
    private int _deferredBytes;
    private bool _removed;
    private TaskCompletionSource _writableChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IChannelHandlerContext _context;
    private Action<Task, object?> _delayThenWrite;
    private Action<Task, object?> _observeWriteCompletion;

    // Thread-safe: Netty guarantees single-threaded channel event delivery,
    // so ??= is never racing on these fields.
    private Action<Task, object?> DelayThenWriteAction => _delayThenWrite ??= DelayThenWrite;
    private Action<Task, object?> ObserveWriteCompletionAction => _observeWriteCompletion ??= ObserveWriteCompletion;

    public int Enqueue<T>(T message) where T : P2PMessage
    {
        lock (_deferredLock)
        {
            if (_removed || !_context.Channel.Active) return 0;
            bool deferred = !_context.Channel.IsWritable || _deferred.Count != 0;
            // Bulk proof gossip retries from its own bounded latest view. Reserve the
            // channel's deferred budget for eth, snap and session control traffic.
            if (deferred && ((message.Protocol == "lean" && message.PacketType == 1) || _deferred.Count == MaxDeferredMessages
                || _deferredBytes == MaxDeferredBytes)) return 0;

            IByteBuffer buffer = _messageSerializationService.ZeroSerialize(message, allocator: _context.Allocator);
            int length = buffer.ReadableBytes;
            if (deferred)
            {
                if (length > MaxDeferredBytes - _deferredBytes)
                {
                    buffer.Release();
                    return 0;
                }
                _deferred.Enqueue(buffer);
                _deferredBytes += length;
                if (_context.Channel.IsWritable) DrainDeferred();
            }
            else SendBuffer(buffer);

            return length;
        }
    }

    /// <inheritdoc/>
    public async ValueTask<int> EnqueueAsync<T>(T message, CancellationToken cancellationToken) where T : P2PMessage
    {
        if (_sendLatency != TimeSpan.Zero) await Task.Delay(_sendLatency, cancellationToken).ConfigureAwait(false);
        Task write;
        int length;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task wait;
            lock (_deferredLock)
            {
                if (_removed || !_context.Channel.Active) return 0;
                if (_context.Channel.IsWritable && _deferred.Count == 0)
                {
                    IByteBuffer buffer = _messageSerializationService.ZeroSerialize(message, allocator: _context.Allocator);
                    length = buffer.ReadableBytes;
                    // The pipeline owns the buffer once WriteAndFlushAsync is called.
                    write = _context.WriteAndFlushAsync(buffer);
                    break;
                }
                wait = _writableChanged.Task;
            }
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        await write.WaitAsync(cancellationToken).ConfigureAwait(false);
        // A bulk producer must return to the scheduler between actual writes.
        await Task.Yield();
        return length;
    }

    private void WakeBulkWriters()
    {
        TaskCompletionSource previous = _writableChanged;
        _writableChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }

    private void DrainDeferred()
    {
        while (!_removed && _context.Channel.Active && _context.Channel.IsWritable && _deferred.TryDequeue(out IByteBuffer? buffer))
        {
            _deferredBytes -= buffer.ReadableBytes;
            SendBuffer(buffer);
        }
        if (_deferred.Count == 0 && _context.Channel.IsWritable) WakeBulkWriters();
    }

    public override void ChannelWritabilityChanged(IChannelHandlerContext context)
    {
        lock (_deferredLock)
        {
            DrainDeferred();
            WakeBulkWriters();
        }
        context.FireChannelWritabilityChanged();
    }

    private void SendBuffer(IByteBuffer buffer)
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
            buffer.Release();
            HandleSendFailure(exception);
            return;
        }

        StartWrite(buffer);
    }

    private void DelayThenWrite(Task delayTask, object? state)
    {
        if (delayTask.IsFaulted)
        {
            ((IByteBuffer)state!).Release();
            HandleSendFailure(delayTask.Exception?.GetBaseException() ?? delayTask.Exception!);
            return;
        }

        if (delayTask.IsCanceled)
        {
            ((IByteBuffer)state!).Release();
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

    public override void ChannelInactive(IChannelHandlerContext context)
    {
        lock (_deferredLock) WakeBulkWriters();
        context.FireChannelInactive();
    }

    public override void HandlerAdded(IChannelHandlerContext context) => _context = context;

    public override void HandlerRemoved(IChannelHandlerContext context)
    {
        lock (_deferredLock)
        {
            _removed = true;
            WakeBulkWriters();
            while (_deferred.TryDequeue(out IByteBuffer? buffer)) buffer.Release();
            _deferredBytes = 0;
        }
        _cts.Cancel();
        _cts.Dispose();
    }
}
