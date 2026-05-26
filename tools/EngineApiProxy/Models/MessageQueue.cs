// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Threading.Channels;
using Nethermind.Logging;

namespace Nethermind.EngineApiProxy.Models;

/// <summary>
/// Manages the queuing and processing of intercepted messages in the proxy.
/// Producers enqueue via <see cref="EnqueueMessage"/>; the single consumer awaits
/// <see cref="DequeueNextMessageAsync"/>, which blocks asynchronously until a message
/// is available and processing is not paused — no busy-polling.
/// </summary>
public class MessageQueue(ILogManager logManager)
{
    private readonly ILogger _logger = logManager.GetClassLogger<MessageQueue>();

    private readonly Channel<QueuedMessage> _channel = Channel.CreateUnbounded<QueuedMessage>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    private readonly ConcurrentDictionary<string, QueuedMessage> _messageById = new();

    // 0 = running, 1 = paused — atomically toggled via Interlocked.CompareExchange
    private int _processingPaused;

    // Completed when processing is not paused. Replaced with a fresh uncompleted TCS on pause,
    // and completed on resume so that any awaiter wakes up.
    private TaskCompletionSource _resumeSignal = CreateCompletedSignal();

    private static TaskCompletionSource CreateCompletedSignal()
    {
        TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }

    /// <summary>
    /// Pauses message processing
    /// </summary>
    public void PauseProcessing()
    {
        if (Interlocked.CompareExchange(ref _processingPaused, 1, 0) == 0)
        {
            Interlocked.Exchange(ref _resumeSignal, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            _logger.Debug("Message processing paused");
        }
    }

    /// <summary>
    /// Resumes message processing
    /// </summary>
    public void ResumeProcessing()
    {
        if (Interlocked.CompareExchange(ref _processingPaused, 0, 1) == 1)
        {
            Volatile.Read(ref _resumeSignal).TrySetResult();
            _logger.Debug("Message processing resumed");
        }
    }

    /// <summary>
    /// Checks if processing is currently paused
    /// </summary>
    public bool IsProcessingPaused => Volatile.Read(ref _processingPaused) == 1;

    /// <summary>
    /// Enqueues a message for delayed processing
    /// </summary>
    /// <param name="message">The message to enqueue</param>
    /// <returns>A task that will complete when the message is processed</returns>
    public Task<JsonRpcResponse> EnqueueMessage(JsonRpcRequest message)
    {
        ArgumentNullException.ThrowIfNull(message);

        QueuedMessage queuedMessage = new(message);
        _channel.Writer.TryWrite(queuedMessage);

        if (message.Id is not null)
        {
            string messageId = message.Id.ToString()!;
            _messageById[messageId] = queuedMessage;
        }

        _logger.Debug($"Enqueued message: {message.Method} with id {message.Id}");

        return queuedMessage.CompletionTask.Task;
    }

    /// <summary>
    /// Asynchronously dequeues the next message from the queue, waiting if the queue is empty
    /// or processing is paused. Returns null only when <paramref name="cancellationToken"/> is
    /// signalled or the channel is completed.
    /// </summary>
    public async ValueTask<QueuedMessage?> DequeueNextMessageAsync(CancellationToken cancellationToken)
    {
        while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (IsProcessingPaused)
            {
                await Volatile.Read(ref _resumeSignal).Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (_channel.Reader.TryRead(out QueuedMessage? message))
            {
                if (message.Request.Id is not null)
                {
                    string messageId = message.Request.Id.ToString()!;
                    _messageById.TryRemove(messageId, out _);
                }

                string? host = null;
                if (message.Request.OriginalHeaders is not null)
                {
                    message.Request.OriginalHeaders.TryGetValue("Host", out host);
                }
                _logger.Debug($"Dequeued message: {message.Request.Method} with id {message.Request.Id} from {host}");
                return message;
            }
        }

        return null;
    }

    /// <summary>
    /// Checks if a message with the given ID is queued
    /// </summary>
    /// <param name="id">The message ID to check</param>
    /// <returns>True if a message with the ID is queued, false otherwise</returns>
    public bool IsMessageQueued(object? id)
    {
        if (id is null)
        {
            return false;
        }

        string messageId = id.ToString() ?? string.Empty;
        return _messageById.ContainsKey(messageId);
    }

    /// <summary>
    /// Gets a message with the given ID from the queue
    /// </summary>
    /// <param name="id">The message ID to get</param>
    /// <returns>The queued message, or null if not found</returns>
    public QueuedMessage? GetMessage(object? id)
    {
        if (id is null)
        {
            return null;
        }

        string messageId = id.ToString() ?? string.Empty;
        return _messageById.TryGetValue(messageId, out QueuedMessage? message) ? message : null;
    }

    /// <summary>
    /// Clears all messages from the queue, cancelling each pending awaiter so that callers
    /// blocked on <see cref="EnqueueMessage"/> are unblocked instead of hanging forever.
    /// </summary>
    public void Clear()
    {
        int cancelled = 0;
        while (_channel.Reader.TryRead(out QueuedMessage? message))
        {
            if (message.CompletionTask.TrySetCanceled())
            {
                cancelled++;
            }
        }
        _messageById.Clear();
        _logger.Debug($"Message queue cleared ({cancelled} pending requests cancelled)");
    }

    /// <summary>
    /// Marks the queue as completed: no further messages may be enqueued. Drains any messages
    /// already in the channel and cancels their pending awaiters so they don't hang during
    /// shutdown. After completion the consumer's <see cref="DequeueNextMessageAsync"/> returns
    /// null once all queued items have been read.
    /// </summary>
    public void Complete()
    {
        _channel.Writer.TryComplete();
        Clear();

        // Wake any consumer currently awaiting the resume signal so it can observe completion.
        Volatile.Read(ref _resumeSignal).TrySetResult();
    }
}

/// <summary>
/// Represents a message queued for processing
/// </summary>
public class QueuedMessage(JsonRpcRequest request)
{
    /// <summary>
    /// The original request
    /// </summary>
    public JsonRpcRequest Request { get; } = request;

    /// <summary>
    /// Task source for completing the message
    /// </summary>
    public TaskCompletionSource<JsonRpcResponse> CompletionTask { get; } = new TaskCompletionSource<JsonRpcResponse>();

    /// <summary>
    /// When the message was enqueued
    /// </summary>
    public DateTime EnqueuedTime { get; } = DateTime.UtcNow;
}
