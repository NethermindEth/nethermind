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
    private sealed class BulkSendState
    {
        public readonly Lock Gate = new();
        public readonly Queue<IByteBuffer> Deferred = [];
        public int DeferredBytes;
        public TaskCompletionSource WritableChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private BulkSendState? _bulk;
    private volatile bool _removed;
    private IChannelHandlerContext _context;
    private Action<Task, object?> _delayThenWrite;
    private Action<Task, object?> _observeWriteCompletion;

    // Thread-safe: Netty guarantees single-threaded channel event delivery,
    // so ??= is never racing on these fields.
    private Action<Task, object?> DelayThenWriteAction => _delayThenWrite ??= DelayThenWrite;
    private Action<Task, object?> ObserveWriteCompletionAction => _observeWriteCompletion ??= ObserveWriteCompletion;

    internal void EnableLeanBulk() => Interlocked.CompareExchange(ref _bulk, new BulkSendState(), null);

    public int Enqueue<T>(T message) where T : P2PMessage
    {
        BulkSendState? bulk = Volatile.Read(ref _bulk);
        if (bulk is not null) return EnqueueWithBulkTraffic(message, bulk);
        if (_removed || !_context.Channel.IsWritable || !_context.Channel.Active) return 0;

        IByteBuffer buffer = _messageSerializationService.ZeroSerialize(message, allocator: _context.Allocator);
        int length = buffer.ReadableBytes;
        SendBuffer(buffer);
        return length;
    }

    private int EnqueueWithBulkTraffic<T>(T message, BulkSendState bulk) where T : P2PMessage
    {
        lock (bulk.Gate)
        {
            if (_removed || !_context.Channel.Active) return 0;
            bool deferred = !_context.Channel.IsWritable || bulk.Deferred.Count != 0;
            // Chunk producers retry independently; reserve the deferred budget for control traffic.
            if (deferred && ((message.Protocol == "lean" && message.PacketType == 1)
                || bulk.Deferred.Count == MaxDeferredMessages || bulk.DeferredBytes == MaxDeferredBytes)) return 0;

            IByteBuffer buffer = _messageSerializationService.ZeroSerialize(message, allocator: _context.Allocator);
            int length = buffer.ReadableBytes;
            if (deferred)
            {
                if (length > MaxDeferredBytes - bulk.DeferredBytes)
                {
                    buffer.Release();
                    return 0;
                }
                bulk.Deferred.Enqueue(buffer);
                bulk.DeferredBytes += length;
                if (_context.Channel.IsWritable) DrainDeferred(bulk);
            }
            else SendBuffer(buffer);
            return length;
        }
    }

    internal async ValueTask<int> EnqueueAsync<T>(T message, CancellationToken cancellationToken) where T : P2PMessage
    {
        BulkSendState bulk = Volatile.Read(ref _bulk) ?? throw new InvalidOperationException("Lean bulk transport is not enabled");
        if (_sendLatency != TimeSpan.Zero) await Task.Delay(_sendLatency, cancellationToken).ConfigureAwait(false);
        Task write;
        int length;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task wait;
            lock (bulk.Gate)
            {
                if (_removed || !_context.Channel.Active) return 0;
                if (_context.Channel.IsWritable && bulk.Deferred.Count == 0)
                {
                    IByteBuffer buffer = _messageSerializationService.ZeroSerialize(message, allocator: _context.Allocator);
                    length = buffer.ReadableBytes;
                    // The pipeline owns the buffer once WriteAndFlushAsync is called.
                    write = _context.WriteAndFlushAsync(buffer);
                    break;
                }
                wait = bulk.WritableChanged.Task;
            }
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        await write.WaitAsync(cancellationToken).ConfigureAwait(false);
        await Task.Yield();
        return length;
    }

    private static void WakeBulkWriters(BulkSendState bulk)
    {
        TaskCompletionSource previous = bulk.WritableChanged;
        bulk.WritableChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }

    private void DrainDeferred(BulkSendState bulk)
    {
        while (!_removed && _context.Channel.Active && _context.Channel.IsWritable && bulk.Deferred.TryDequeue(out IByteBuffer? buffer))
        {
            bulk.DeferredBytes -= buffer.ReadableBytes;
            SendBuffer(buffer);
        }
        if (bulk.Deferred.Count == 0 && _context.Channel.IsWritable) WakeBulkWriters(bulk);
    }

    public override void ChannelWritabilityChanged(IChannelHandlerContext context)
    {
        if (Volatile.Read(ref _bulk) is { } bulk)
            lock (bulk.Gate)
            {
                DrainDeferred(bulk);
                WakeBulkWriters(bulk);
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
        if (Volatile.Read(ref _bulk) is { } bulk)
            lock (bulk.Gate) WakeBulkWriters(bulk);
        context.FireChannelInactive();
    }

    public override void HandlerAdded(IChannelHandlerContext context) => _context = context;

    public override void HandlerRemoved(IChannelHandlerContext context)
    {
        _removed = true;
        if (Volatile.Read(ref _bulk) is { } bulk)
            lock (bulk.Gate)
            {
                WakeBulkWriters(bulk);
                while (bulk.Deferred.TryDequeue(out IByteBuffer? buffer)) buffer.Release();
                bulk.DeferredBytes = 0;
            }
        _cts.Cancel();
        _cts.Dispose();
    }
}
