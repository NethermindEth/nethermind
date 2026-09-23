// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.ObjectPool;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Logging;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.TxPool;

public sealed class RetryCache<TMessage, TResourceId> : IAsyncDisposable
    where TMessage : INew<TResourceId, TMessage>
    where TResourceId : struct, IEquatable<TResourceId>, IHash64bit<TResourceId>
{
    private const int DefaultTimeoutMs = 3500;
    private const int DefaultRequestingCacheSize = 1024;
    private const int DefaultMaxRetryRequests = 4;
    private const int DefaultMaxPendingResourcesPerHandler = 4096;
    private const int DefaultTrackedHandlerCapacity = 50;
    private const int DefaultExpiringQueueLimit = DefaultMaxPendingResourcesPerHandler * DefaultTrackedHandlerCapacity;
    internal const int DefaultOverflowRequestLimit = 500_000;
    private const int MaxRetryResourcesPerTick = 256;
    private const int MaxStaleQueueEntriesPerTick = 32_768;
    private const int AdmissionLockCount = 64;
    private const int MaxRetainedHandlerBags = 2048;

    private readonly int _timeoutMs;
    private readonly TimeSpan _timeout;
    private readonly CancellationTokenSource _lifetimeCancellation;
    private readonly CancellationToken _token;
    private readonly int _checkMs;
    private readonly int _expiringQueueLimit;
    private readonly int _expiringQueuePhysicalLimit;
    private readonly int _maxRetryRequests;
    private readonly int _maxPendingResourcesPerHandler;
    private readonly int _overflowRequestLimit;
    private readonly long _overflowGenerationPeriodTimestampTicks;
    private readonly int _maxPreferredRetryResourcesPerHandlerPerTick;
    private readonly TimeProvider _timeProvider;
    private readonly Task _mainLoopTask;
    private static readonly ObjectPool<HandlerBag<TMessage>> _handlerBagsPool = new DefaultObjectPool<HandlerBag<TMessage>>(new HandlerBagPolicy<TMessage>(), maximumRetained: MaxRetainedHandlerBags);
    private readonly RetryRequestStore _retryRequests = new();
    private readonly ConditionalWeakTable<IMessageHandler<TMessage>, PendingHandlerCount> _pendingResourcesByHandler = [];
    private RetryBatches? _batchedRetryRequests;
    private readonly ExpiringQueue _expiringQueue = new();
    private readonly OverflowRequestStripe[]? _overflowRequestStripes;
    private readonly int[]? _overflowRequestGenerationCounts;
    private readonly Lock[]? _admissionLocks;
    private readonly ReaderWriterLockSlim _overflowGenerationLock = new(LockRecursionPolicy.NoRecursion);
    private int _expiringQueueCounter = 0;
    private int _trackedRequestsCounter = 0;
    private long _requestGeneration = 0;
    private long _overflowEpoch = 0;
    private int _overflowRequestsInUse = 0;
    private long _overflowGenerationStartedAt;
    private int _activeOperations = 0;
    private int _disposeState = 0;
    private readonly TaskCompletionSource _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _disposeReachedOperationBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AssociativeKeyCache<TResourceId> _requestingResources;
    private readonly ILogger _logger;

    internal int ResourcesInRetryQueue => Math.Max(0, Volatile.Read(ref _expiringQueueCounter));
    internal int ExpiringQueueCapacity => _expiringQueue.Capacity;
    internal int PendingHandlersInUse
    {
        get
        {
            int count = 0;
            foreach (KeyValuePair<IMessageHandler<TMessage>, PendingHandlerCount> entry in _pendingResourcesByHandler)
            {
                if (entry.Value.Count > 0) count++;
            }
            return count;
        }
    }
    internal int RetainedBatchCapacity => Volatile.Read(ref _batchedRetryRequests)?.Capacity ?? 0;
    internal int TrackedRequestsInUse => Volatile.Read(ref _trackedRequestsCounter);
    internal int RetryRequestRetainedCapacity => _retryRequests.RetainedCapacity;
    internal int OverflowRequestsInUse => Volatile.Read(ref _overflowRequestsInUse);
    internal Task DisposalReachedOperationBarrier => _disposeReachedOperationBarrier.Task;
    internal int OverflowRetainedCapacity
    {
        get
        {
            if (_overflowRequestStripes is null)
            {
                return 0;
            }

            int capacity = 0;
            for (int i = 0; i < _overflowRequestStripes.Length; i++)
            {
                lock (_admissionLocks![i])
                {
                    capacity += _overflowRequestStripes[i].Generations[0].EnsureCapacity(0);
                    capacity += _overflowRequestStripes[i].Generations[1].EnsureCapacity(0);
                }
            }

            return capacity;
        }
    }

    private readonly record struct RetryRequestEntry(
        HandlerBag<TMessage> Handlers,
        long Generation,
        IMessageHandler<TMessage> SourceHandler,
        long RequestGeneration);

    private sealed class PendingHandlerCount
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public bool TryReserve(int limit) => TryReserveSlot(ref _count, limit);

        public void Release()
        {
            int count = Volatile.Read(ref _count);
            while (count > 0)
            {
                int observed = Interlocked.CompareExchange(ref _count, count - 1, count);
                if (observed == count) return;
                count = observed;
            }
        }
    }

    private sealed class RetryBatches
    {
        public readonly Dictionary<IBatchMessageHandler<TMessage, TResourceId>, List<TResourceId>> Requests = new(ReferenceEqualityComparer.Instance);
        private readonly List<List<TResourceId>> _buffers = [];

        public int Capacity
        {
            get
            {
                int capacity = 0;
                foreach (List<TResourceId> buffer in _buffers) capacity += buffer.Capacity;
                return capacity;
            }
        }

        public void Add(IBatchMessageHandler<TMessage, TResourceId> handler, TResourceId resourceId)
        {
            if (!Requests.TryGetValue(handler, out List<TResourceId>? resources))
            {
                int index = Requests.Count;
                if (index == _buffers.Count) _buffers.Add([]);
                resources = _buffers[index];
                Requests.Add(handler, resources);
            }
            resources.Add(resourceId);
        }

        public bool Reset()
        {
            Requests.Clear();
            foreach (List<TResourceId> buffer in _buffers)
            {
                buffer.Clear();
            }
            return Capacity <= MaxRetryResourcesPerTick * 4;
        }
    }

    private sealed class RetryRequestStore
    {
        private const int MaxRetainedStripeCapacity = 1024;
        private readonly Stripe[] _stripes = CreateStripes();

        public int RetainedCapacity
        {
            get
            {
                int capacity = 0;
                foreach (Stripe stripe in _stripes)
                {
                    lock (stripe.Sync) capacity += stripe.Entries.EnsureCapacity(0);
                }
                return capacity;
            }
        }

        private static Stripe[] CreateStripes()
        {
            Stripe[] stripes = new Stripe[AdmissionLockCount];
            for (int i = 0; i < stripes.Length; i++) stripes[i] = new Stripe();
            return stripes;
        }

        private Stripe GetStripe(in TResourceId resourceId) => _stripes[(int)resourceId.GetHashCode64() & (_stripes.Length - 1)];

        public bool TryGetValue(in TResourceId resourceId, out RetryRequestEntry entry)
        {
            Stripe stripe = GetStripe(resourceId);
            lock (stripe.Sync) return stripe.Entries.TryGetValue(resourceId, out entry);
        }

        public bool ContainsKey(in TResourceId resourceId) => TryGetValue(resourceId, out _);

        public bool TryAdd(in TResourceId resourceId, RetryRequestEntry entry)
        {
            Stripe stripe = GetStripe(resourceId);
            lock (stripe.Sync) return stripe.Entries.TryAdd(resourceId, entry);
        }

        public bool TryRemove(in TResourceId resourceId, out RetryRequestEntry entry)
        {
            Stripe stripe = GetStripe(resourceId);
            lock (stripe.Sync)
            {
                bool removed = stripe.Entries.Remove(resourceId, out entry);
                if (removed) TrimEmptyStripe(stripe);
                return removed;
            }
        }

        public bool TryRemove(KeyValuePair<TResourceId, RetryRequestEntry> item)
        {
            Stripe stripe = GetStripe(item.Key);
            lock (stripe.Sync)
            {
                bool removed = stripe.Entries.TryGetValue(item.Key, out RetryRequestEntry entry)
                    && entry == item.Value
                    && stripe.Entries.Remove(item.Key);
                if (removed) TrimEmptyStripe(stripe);
                return removed;
            }
        }

        private static void TrimEmptyStripe(Stripe stripe)
        {
            if (stripe.Entries.Count == 0 && stripe.Entries.EnsureCapacity(0) > MaxRetainedStripeCapacity)
            {
                stripe.Entries.TrimExcess();
            }
        }

        public List<KeyValuePair<TResourceId, RetryRequestEntry>> GetSnapshot()
        {
            List<KeyValuePair<TResourceId, RetryRequestEntry>> entries = [];
            foreach (Stripe stripe in _stripes)
            {
                lock (stripe.Sync)
                {
                    foreach (KeyValuePair<TResourceId, RetryRequestEntry> entry in stripe.Entries) entries.Add(entry);
                }
            }

            return entries;
        }

        private sealed class Stripe
        {
            public readonly Lock Sync = new();
            public readonly Dictionary<TResourceId, RetryRequestEntry> Entries = [];
        }
    }

    private sealed class ExpiringQueue
    {
        private const int MaxRetainedCapacity = 16_384;
        private readonly Lock _lock = new();
        private readonly Queue<(TResourceId ResourceId, long RequestGeneration, long EnqueuedAt)> _queue = new();
        private int _lowUsageTicks;

        public int Capacity
        {
            get
            {
                lock (_lock) return _queue.EnsureCapacity(0);
            }
        }

        public void TrimExcess()
        {
            lock (_lock)
            {
                if (_queue.Count > MaxRetainedCapacity / 2 || _queue.EnsureCapacity(0) <= MaxRetainedCapacity)
                {
                    _lowUsageTicks = 0;
                    return;
                }
                // Keep burst capacity until demand has stayed low across several retry ticks.
                if (++_lowUsageTicks >= 3)
                {
                    _queue.TrimExcess();
                    _lowUsageTicks = 0;
                }
            }
        }

        public void Enqueue((TResourceId ResourceId, long RequestGeneration, long EnqueuedAt) item)
        {
            lock (_lock)
            {
                _queue.Enqueue(item);
                if (_queue.Count > MaxRetainedCapacity / 2) _lowUsageTicks = 0;
            }
        }

        public bool TryDequeueExpired(TimeProvider timeProvider, TimeSpan timeout,
            out (TResourceId ResourceId, long RequestGeneration, long EnqueuedAt) item)
        {
            lock (_lock)
            {
                if (!_queue.TryPeek(out item) || timeProvider.GetElapsedTime(item.EnqueuedAt) < timeout) return false;
                _queue.Dequeue();
                return true;
            }
        }

        public bool TryDequeue(out (TResourceId ResourceId, long RequestGeneration, long EnqueuedAt) item)
        {
            lock (_lock) return _queue.TryDequeue(out item);
        }
    }

    private sealed class OverflowRequestStripe(int initialGenerationCapacity)
    {
        private const int MaxRetainedGenerationCapacity = 1_024;
        private const int MaxReusedGenerationCapacity = 16_384;
        // HashSet grows 431 to 919, still below the retention cap; requesting 512 rounds to 521 and grows to 1103.
        public const int MaxWarmSpareCapacity = 431;

        public readonly HashSet<TResourceId>[] Generations = [[], []];
        public readonly int RetainedGenerationCapacityLimit = Math.Min(
            initialGenerationCapacity * 2,
            MaxRetainedGenerationCapacity);
        public readonly int ReusedGenerationCapacityLimit = Math.Min(initialGenerationCapacity * 2, MaxReusedGenerationCapacity);
        public readonly long[] Epochs = [0, -1];
        public readonly bool[] WarmSpare = new bool[2];
        public readonly bool[] ReusedWarmSpare = new bool[2];
    }

    public RetryCache(
        ILogManager logManager,
        int timeoutMs = DefaultTimeoutMs,
        int requestingCacheSize = DefaultRequestingCacheSize,
        int expiringQueueLimit = DefaultExpiringQueueLimit,
        int maxRetryRequests = DefaultMaxRetryRequests,
        CancellationToken token = default,
        int maxPendingResourcesPerHandler = DefaultMaxPendingResourcesPerHandler)
        : this(
            logManager,
            TimeProvider.System,
            timeoutMs,
            requestingCacheSize,
            expiringQueueLimit,
            maxRetryRequests,
            maxPendingResourcesPerHandler,
            overflowRequestLimit: 0,
            token: token)
    {
    }

    internal RetryCache(
        ILogManager logManager,
        TimeProvider timeProvider,
        int timeoutMs = DefaultTimeoutMs,
        int requestingCacheSize = DefaultRequestingCacheSize,
        int expiringQueueLimit = DefaultExpiringQueueLimit,
        int maxRetryRequests = DefaultMaxRetryRequests,
        int maxPendingResourcesPerHandler = DefaultMaxPendingResourcesPerHandler,
        int overflowRequestLimit = 0,
        CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requestingCacheSize);
        _logger = logManager.GetClassLogger(typeof(RetryCache<,>));

        _timeoutMs = timeoutMs;
        _timeout = TimeSpan.FromMilliseconds(timeoutMs);
        _checkMs = Math.Max(1, _timeoutMs / 5);
        _requestingResources = new(requestingCacheSize);
        _expiringQueueLimit = expiringQueueLimit;
        _maxRetryRequests = maxRetryRequests;
        _maxPendingResourcesPerHandler = maxPendingResourcesPerHandler;
        _overflowRequestLimit = Math.Max(0, overflowRequestLimit);
        _overflowGenerationPeriodTimestampTicks = Math.Max(
            1,
            (long)Math.Ceiling(_timeout.TotalSeconds * timeProvider.TimestampFrequency));
        _expiringQueuePhysicalLimit = (int)Math.Min(int.MaxValue, (long)_expiringQueueLimit + _overflowRequestLimit);
        _overflowRequestStripes = _overflowRequestLimit > 0 ? CreateOverflowRequestStripes(_overflowRequestLimit) : null;
        _overflowRequestGenerationCounts = _overflowRequestLimit > 0 ? new int[2] : null;
        _admissionLocks = _overflowRequestLimit > 0 ? CreateAdmissionLocks() : null;
        _overflowGenerationStartedAt = timeProvider.GetTimestamp();
        _maxPreferredRetryResourcesPerHandlerPerTick = Math.Max(
            1,
            MaxRetryResourcesPerTick / Math.Max(1, maxRetryRequests));
        _timeProvider = timeProvider;
        _lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _token = _lifetimeCancellation.Token;
        try
        {
            _mainLoopTask = Task.Run(RunAsync, _token);
        }
        catch
        {
            _lifetimeCancellation.Dispose();
            throw;
        }
    }

    private async Task RunAsync()
    {
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(_checkMs), _timeProvider);

        while (await timer.WaitForNextTickAsync(_token))
        {
            ProcessRetryTick();
        }

        if (_logger.IsDebug) _logger.Debug($"{typeof(TResourceId)} retry cache stopped");
    }

    internal void ProcessRetryTick()
    {
        RetryBatches? batchedRetryRequests = null;
        try
        {
            int retryResourcesProcessed = 0;
            int queueEntriesProcessed = 0;
            int queueEntriesToProcess = Math.Min(ResourcesInRetryQueue, MaxStaleQueueEntriesPerTick);
            while (!_token.IsCancellationRequested
                && retryResourcesProcessed < MaxRetryResourcesPerTick
                && queueEntriesProcessed < queueEntriesToProcess
                && _expiringQueue.TryDequeueExpired(_timeProvider, _timeout, out (TResourceId ResourceId, long RequestGeneration, long EnqueuedAt) item))
            {
                queueEntriesProcessed++;
                bool queueSlotTransferred = false;

                try
                {
                    if (TryTakeRetryHandler(
                        item,
                        batchedRetryRequests,
                        out RetryRequestEntry currentEntry,
                        out IMessageHandler<TMessage>? retryHandler))
                    {
                        retryResourcesProcessed++;
                        ReleaseHandlerSlot(retryHandler!);
                        Enqueue(item.ResourceId, currentEntry);
                        queueSlotTransferred = true;
                        CollectRetryRequest(item.ResourceId, retryHandler!, ref batchedRetryRequests);
                    }
                }
                finally
                {
                    if (!queueSlotTransferred)
                    {
                        Interlocked.Decrement(ref _expiringQueueCounter);
                    }
                }
            }

            SendBatchedRetryRequests(batchedRetryRequests);
        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error($"Unexpected error in {typeof(TResourceId)} retry cache loop", ex);
            Clear();
        }
        finally
        {
            ReturnBatchedRetryRequests(batchedRetryRequests);
            MaintainOverflowStorage();
            _expiringQueue.TrimExcess();
        }
    }

    private bool TryTakeRetryHandler(
        in (TResourceId ResourceId, long RequestGeneration, long EnqueuedAt) item,
        RetryBatches? batchedRetryRequests,
        out RetryRequestEntry currentEntry,
        out IMessageHandler<TMessage>? retryHandler)
    {
        if (_retryRequests.TryGetValue(item.ResourceId, out currentEntry)
            && currentEntry.RequestGeneration == item.RequestGeneration)
        {
            BatchedHandlerPreference handlerPreference = new(
                batchedRetryRequests?.Requests,
                _maxPreferredRetryResourcesPerHandlerPerTick);
            if (currentEntry.Handlers.TryTake(
                currentEntry.Generation,
                ref handlerPreference,
                out retryHandler,
                out _))
            {
                return true;
            }

            _requestingResources.Set(item.ResourceId);
            if (!TryRemove(item.ResourceId, currentEntry))
            {
                _requestingResources.Delete(item.ResourceId);
            }
        }

        retryHandler = null;
        return false;
    }

    public AnnounceResult Announced(in TResourceId resourceId, IMessageHandler<TMessage> handler)
    {
        if (!TryEnterOperation())
        {
            return AnnounceResult.RequestRequired;
        }

        try
        {
            return AnnouncedCore(resourceId, handler);
        }
        finally
        {
            ExitOperation();
        }
    }

    private AnnounceResult AnnouncedCore(in TResourceId resourceId, IMessageHandler<TMessage> handler)
    {
        if (_token.IsCancellationRequested)
        {
            return AnnounceResult.RequestRequired;
        }

        if (IsOverflowRequestPending(resourceId))
        {
            return AnnounceResult.Delayed;
        }

        while (!_requestingResources.Contains(resourceId))
        {
            if (_retryRequests.TryGetValue(resourceId, out RetryRequestEntry existingEntry))
            {
                if (ReferenceEquals(existingEntry.SourceHandler, handler))
                {
                    return AnnounceResult.Delayed;
                }

                bool candidateSlotReserved = TryReserveHandlerSlot(handler);
                if (!candidateSlotReserved)
                {
                    _logger.TraceWarn($"{handler} has reached the pending {typeof(TResourceId)} limit, suppressing request");
                    return AnnounceResult.Delayed;
                }

                HandlerBagAddResult addResult;
                try
                {
                    addResult = existingEntry.Handlers.Add(handler, _maxRetryRequests, existingEntry.Generation);
                    if (addResult is HandlerBagAddResult.Added)
                    {
                        candidateSlotReserved = false;
                    }
                }
                finally
                {
                    if (candidateSlotReserved)
                    {
                        ReleaseHandlerSlot(handler);
                    }
                }

                if (addResult is not HandlerBagAddResult.Inactive)
                {
                    return AnnounceResult.Delayed;
                }

                if (_retryRequests.TryGetValue(resourceId, out RetryRequestEntry currentEntry)
                    && currentEntry == existingEntry)
                {
                    return AnnounceResult.RequestRequired;
                }

                continue;
            }

            HandlerBag<TMessage>? newBag = null;
            bool published = false;
            bool trackedSlotReserved = false;
            bool queueSlotReserved = false;
            bool handlerSlotReserved = TryReserveHandlerSlot(handler);
            if (!handlerSlotReserved)
            {
                if (TryReserveOverflowRequest(resourceId))
                {
                    return AnnounceResult.RequestRequired;
                }

                _logger.TraceWarn($"Shared pending {typeof(TResourceId)} overflow capacity is full, suppressing request from {handler}");
                return AnnounceResult.Delayed;
            }

            try
            {
                trackedSlotReserved = TryReserveTrackedSlot();
                if (!trackedSlotReserved)
                {
                    _logger.TraceWarn($"{typeof(TResourceId)} tracked request limit is full, bypassing retry tracking");
                    return _overflowRequestStripes is null || TryReserveOverflowRequest(resourceId)
                        ? AnnounceResult.RequestRequired
                        : AnnounceResult.Delayed;
                }

                queueSlotReserved = TryReserveExpiringQueueSlot();
                if (!queueSlotReserved)
                {
                    _logger.TraceWarn($"{typeof(TResourceId)} expiry queue is full, bypassing retry tracking");
                    return _overflowRequestStripes is null || TryReserveOverflowRequest(resourceId)
                        ? AnnounceResult.RequestRequired
                        : AnnounceResult.Delayed;
                }

                newBag = _handlerBagsPool.Get();
                RetryRequestEntry newEntry = new(
                    newBag,
                    newBag.Activate(),
                    handler,
                    Interlocked.Increment(ref _requestGeneration));
                published = TryPublishTrackedRequest(resourceId, newEntry);

                if (published)
                {
                    // First announcer: not added to the retry bag because the caller receives
                    // RequestRequired and will immediately request the resource itself.
                    // Only subsequent announcers (via TryAdd) are registered for retry.
                    Enqueue(resourceId, newEntry);
                    trackedSlotReserved = false;
                    queueSlotReserved = false;
                    handlerSlotReserved = false;
                    return AnnounceResult.RequestRequired;
                }
            }
            finally
            {
                if (queueSlotReserved)
                {
                    Interlocked.Decrement(ref _expiringQueueCounter);
                }

                if (trackedSlotReserved)
                {
                    Interlocked.Decrement(ref _trackedRequestsCounter);
                }

                if (handlerSlotReserved)
                {
                    ReleaseHandlerSlot(handler);
                }

                if (!published && newBag is not null)
                {
                    _handlerBagsPool.Return(newBag);
                }
            }

            if (IsOverflowRequestPending(resourceId))
            {
                return AnnounceResult.Delayed;
            }
        }

        if (_logger.IsTrace) _logger.Trace($"Announced {resourceId} by {handler}, but a retry is in progress already, immediately firing");

        return AnnounceResult.RequestRequired;
    }

    private void Enqueue(TResourceId resourceId, RetryRequestEntry entry) =>
        _expiringQueue.Enqueue((resourceId, entry.RequestGeneration, _timeProvider.GetTimestamp()));

    public void Received(in TResourceId resourceId)
    {
        if (!TryEnterOperation())
        {
            return;
        }

        try
        {
            ReceivedCore(resourceId);
        }
        finally
        {
            ExitOperation();
        }
    }

    private void ReceivedCore(in TResourceId resourceId)
    {
        if (_logger.IsTrace) _logger.Trace($"Received {resourceId}");

        if (_retryRequests.TryRemove(resourceId, out RetryRequestEntry entry))
        {
            Interlocked.Decrement(ref _trackedRequestsCounter);
            Return(entry);
        }

        ReleaseOverflowRequest(resourceId);

        _requestingResources.Delete(resourceId);
    }

    private void Clear()
    {
        while (_expiringQueue.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _expiringQueueCounter);
        }

        _requestingResources.Clear();
        ClearOverflowRequests();

        foreach (KeyValuePair<TResourceId, RetryRequestEntry> kvp in _retryRequests.GetSnapshot())
        {
            if (_retryRequests.TryRemove(kvp))
            {
                Interlocked.Decrement(ref _trackedRequestsCounter);
                Return(kvp.Value);
            }
        }

    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0)
        {
            await _disposeCompletion.Task;
            return;
        }

        Exception? disposeException = null;
        try
        {
            await _lifetimeCancellation.CancelAsync();
            try
            {
                await _mainLoopTask;
            }
            catch (OperationCanceledException) when (_token.IsCancellationRequested) { }

            _disposeReachedOperationBarrier.TrySetResult();
        }
        catch (Exception ex)
        {
            disposeException = ex;
        }
        finally
        {
            while (Volatile.Read(ref _activeOperations) != 0)
            {
                await Task.Yield();
            }

            try
            {
                Clear();
                ReleaseOverflowStorage();
                _batchedRetryRequests = null;
            }
            catch (Exception ex)
            {
                disposeException = disposeException is null ? ex : new AggregateException(disposeException, ex);
            }

            try
            {
                _overflowGenerationLock.Dispose();
            }
            catch (Exception ex)
            {
                disposeException = disposeException is null ? ex : new AggregateException(disposeException, ex);
            }

            _lifetimeCancellation.Dispose();

            Volatile.Write(ref _disposeState, 2);
            if (disposeException is null)
            {
                _disposeCompletion.TrySetResult();
            }
            else
            {
                _disposeCompletion.TrySetException(disposeException);
            }
        }

        if (disposeException is not null)
        {
            ExceptionDispatchInfo.Capture(disposeException).Throw();
        }
    }

    private void SendBatchedRetryRequests(RetryBatches? batchedRetryRequests)
    {
        if (batchedRetryRequests is null)
        {
            return;
        }

        foreach (KeyValuePair<IBatchMessageHandler<TMessage, TResourceId>, List<TResourceId>> kvp in batchedRetryRequests.Requests)
        {
            try
            {
                kvp.Key.HandleMessages(CollectionsMarshal.AsSpan(kvp.Value));
            }
            catch (Exception ex)
            {
                _logger.TraceError($"Failed to send batched retry requests to {kvp.Key} for {kvp.Value.Count} {typeof(TResourceId)} resources", ex);
            }
        }
    }

    private void CollectRetryRequest(
        TResourceId resourceId,
        IMessageHandler<TMessage> retryHandler,
        ref RetryBatches? batchedRetryRequests)
    {
        if (_logger.IsTrace) _logger.Trace($"Sending a retry request for {resourceId} after timeout");

        if (retryHandler is IBatchMessageHandler<TMessage, TResourceId> batchRetryHandler)
        {
            batchedRetryRequests ??= Interlocked.Exchange(ref _batchedRetryRequests, null) ?? new();
            batchedRetryRequests.Add(batchRetryHandler, resourceId);
            return;
        }

        try
        {
            retryHandler.HandleMessage(TMessage.New(resourceId));
        }
        catch (Exception ex)
        {
            _logger.TraceError($"Failed to send retry request to {retryHandler} for {resourceId}", ex);
        }
    }

    private bool TryReserveTrackedSlot() => TryReserveSlot(ref _trackedRequestsCounter, _expiringQueueLimit);

    private bool TryReserveExpiringQueueSlot() => TryReserveSlot(ref _expiringQueueCounter, _expiringQueuePhysicalLimit);

    private static bool TryReserveSlot(ref int counter, int limit)
    {
        int count = Volatile.Read(ref counter);
        while (count < limit)
        {
            int observed = Interlocked.CompareExchange(ref counter, count + 1, count);
            if (observed == count)
            {
                return true;
            }

            count = observed;
        }

        return false;
    }

    private bool TryReserveHandlerSlot(IMessageHandler<TMessage> handler) =>
        _maxPendingResourcesPerHandler > 0
        && _pendingResourcesByHandler.GetValue(handler, static _ => new()).TryReserve(_maxPendingResourcesPerHandler);

    private void ReleaseHandlerSlot(IMessageHandler<TMessage> handler)
    {
        if (_pendingResourcesByHandler.TryGetValue(handler, out PendingHandlerCount? count)) count.Release();
    }

    private bool TryReserveOverflowRequest(in TResourceId resourceId)
    {
        if (_overflowRequestStripes is null)
        {
            return false;
        }

        RotateOverflowGenerations(_timeProvider.GetTimestamp());
        lock (GetAdmissionLock(resourceId))
        {
            _overflowGenerationLock.EnterReadLock();
            try
            {
                long epoch = Volatile.Read(ref _overflowEpoch);
                OverflowRequestStripe stripe = GetOverflowRequestStripe(resourceId);
                PrepareOverflowStripe(stripe, epoch);
                if (_retryRequests.ContainsKey(resourceId)
                    || IsOverflowRequestPendingCore(stripe, resourceId, epoch)
                    || !TryReserveSlot(ref _overflowRequestsInUse, _overflowRequestLimit))
                {
                    return false;
                }

                int generation = (int)(epoch & 1);
                stripe.Generations[generation].Add(resourceId);
                stripe.ReusedWarmSpare[generation] |= stripe.WarmSpare[generation];
                stripe.WarmSpare[generation] = false;
                Interlocked.Increment(ref _overflowRequestGenerationCounts![generation]);
                return true;
            }
            finally
            {
                _overflowGenerationLock.ExitReadLock();
            }
        }
    }

    private bool TryPublishTrackedRequest(in TResourceId resourceId, RetryRequestEntry entry)
    {
        if (_overflowRequestStripes is null)
        {
            return _retryRequests.TryAdd(resourceId, entry);
        }

        RotateOverflowGenerations(_timeProvider.GetTimestamp());
        lock (GetAdmissionLock(resourceId))
        {
            long epoch = Volatile.Read(ref _overflowEpoch);
            OverflowRequestStripe stripe = GetOverflowRequestStripe(resourceId);
            PrepareOverflowStripe(stripe, epoch);
            return !IsOverflowRequestPendingCore(stripe, resourceId, epoch) && _retryRequests.TryAdd(resourceId, entry);
        }
    }

    private void ReleaseOverflowRequest(in TResourceId resourceId)
    {
        if (_overflowRequestStripes is null)
        {
            return;
        }

        RotateOverflowGenerations(_timeProvider.GetTimestamp());
        lock (GetAdmissionLock(resourceId))
        {
            _overflowGenerationLock.EnterReadLock();
            try
            {
                long epoch = Volatile.Read(ref _overflowEpoch);
                OverflowRequestStripe stripe = GetOverflowRequestStripe(resourceId);
                PrepareOverflowStripe(stripe, epoch);
                for (long candidateEpoch = epoch; candidateEpoch >= epoch - 1; candidateEpoch--)
                {
                    int generation = (int)(candidateEpoch & 1);
                    if (stripe.Generations[generation].Remove(resourceId))
                    {
                        Interlocked.Decrement(ref _overflowRequestGenerationCounts![generation]);
                        Interlocked.Decrement(ref _overflowRequestsInUse);
                        return;
                    }
                }
            }
            finally
            {
                _overflowGenerationLock.ExitReadLock();
            }
        }
    }

    private bool IsOverflowRequestPending(in TResourceId resourceId)
    {
        if (_overflowRequestStripes is null)
        {
            return false;
        }

        RotateOverflowGenerations(_timeProvider.GetTimestamp());
        lock (GetAdmissionLock(resourceId))
        {
            long epoch = Volatile.Read(ref _overflowEpoch);
            OverflowRequestStripe stripe = GetOverflowRequestStripe(resourceId);
            PrepareOverflowStripe(stripe, epoch);
            return IsOverflowRequestPendingCore(stripe, resourceId, epoch);
        }
    }

    private static bool IsOverflowRequestPendingCore(OverflowRequestStripe stripe, in TResourceId resourceId, long epoch) =>
        stripe.Generations[(int)(epoch & 1)].Contains(resourceId)
        || stripe.Generations[(int)((epoch - 1) & 1)].Contains(resourceId);

    private void RotateOverflowGenerations(long now)
    {
        if (now - Volatile.Read(ref _overflowGenerationStartedAt) < _overflowGenerationPeriodTimestampTicks)
        {
            return;
        }

        _overflowGenerationLock.EnterWriteLock();
        try
        {
            RotateOverflowGenerationsCore(now);
        }
        finally
        {
            _overflowGenerationLock.ExitWriteLock();
        }
    }

    private void RotateOverflowGenerationsCore(long now)
    {
        long elapsed = now - _overflowGenerationStartedAt;
        if (elapsed < _overflowGenerationPeriodTimestampTicks)
        {
            return;
        }

        long completedPeriods = elapsed / _overflowGenerationPeriodTimestampTicks;
        Volatile.Write(
            ref _overflowGenerationStartedAt,
            _overflowGenerationStartedAt + completedPeriods * _overflowGenerationPeriodTimestampTicks);
        if (completedPeriods >= 2)
        {
            _overflowRequestGenerationCounts![0] = 0;
            _overflowRequestGenerationCounts[1] = 0;
            _overflowRequestsInUse = 0;
            Volatile.Write(ref _overflowEpoch, _overflowEpoch + completedPeriods);
            return;
        }

        long nextEpoch = _overflowEpoch + 1;
        int nextGeneration = (int)(nextEpoch & 1);
        _overflowRequestsInUse -= _overflowRequestGenerationCounts![nextGeneration];
        _overflowRequestGenerationCounts[nextGeneration] = 0;
        Volatile.Write(ref _overflowEpoch, nextEpoch);
    }

    private static void PrepareOverflowStripe(OverflowRequestStripe stripe, long epoch)
    {
        PrepareOverflowGeneration(stripe, epoch);
        PrepareOverflowGeneration(stripe, epoch - 1);
    }

    private static void PrepareOverflowGeneration(OverflowRequestStripe stripe, long epoch)
    {
        int generation = (int)(epoch & 1);
        if (stripe.Epochs[generation] != epoch)
        {
            int capacity = stripe.Generations[generation].EnsureCapacity(0);
            bool retainReusedCapacity = stripe.ReusedWarmSpare[generation]
                && epoch - stripe.Epochs[generation] == 2
                && capacity <= stripe.ReusedGenerationCapacityLimit;
            if (stripe.WarmSpare[generation])
            {
                stripe.Generations[generation] = [];
                stripe.WarmSpare[generation] = false;
            }
            else if (capacity > stripe.RetainedGenerationCapacityLimit && !retainReusedCapacity)
            {
                // Resume at an existing growth step, without retaining a flood-sized set.
                capacity = stripe.RetainedGenerationCapacityLimit >= 3
                    ? Math.Min(OverflowRequestStripe.MaxWarmSpareCapacity, stripe.RetainedGenerationCapacityLimit / 2)
                    : 0;
                stripe.Generations[generation] = new(capacity);
                stripe.WarmSpare[generation] = capacity > 0;
            }
            else
            {
                stripe.Generations[generation].Clear();
                // Repeated demand keeps bounded capacity for one more use; an unused spare is released next rotation.
                stripe.WarmSpare[generation] = capacity > stripe.RetainedGenerationCapacityLimit;
            }

            stripe.ReusedWarmSpare[generation] = false;
            stripe.Epochs[generation] = epoch;
        }
    }

    private void MaintainOverflowStorage()
    {
        if (_overflowRequestStripes is null)
        {
            return;
        }

        RotateOverflowGenerations(_timeProvider.GetTimestamp());
        for (int i = 0; i < _overflowRequestStripes.Length; i++)
        {
            lock (_admissionLocks![i])
            {
                _overflowGenerationLock.EnterReadLock();
                try
                {
                    PrepareOverflowStripe(_overflowRequestStripes[i], Volatile.Read(ref _overflowEpoch));
                }
                finally
                {
                    _overflowGenerationLock.ExitReadLock();
                }
            }
        }
    }

    private OverflowRequestStripe GetOverflowRequestStripe(in TResourceId resourceId)
    {
        int index = (int)resourceId.GetHashCode64() & (AdmissionLockCount - 1);
        return _overflowRequestStripes![index];
    }

    private Lock GetAdmissionLock(in TResourceId resourceId)
    {
        int index = (int)resourceId.GetHashCode64() & (AdmissionLockCount - 1);
        return _admissionLocks![index];
    }

    private void ClearOverflowRequests()
    {
        if (_overflowRequestStripes is null)
        {
            return;
        }

        _overflowGenerationLock.EnterWriteLock();
        try
        {
            _overflowRequestGenerationCounts![0] = 0;
            _overflowRequestGenerationCounts[1] = 0;
            _overflowRequestsInUse = 0;
            Volatile.Write(ref _overflowGenerationStartedAt, _timeProvider.GetTimestamp());
            Volatile.Write(ref _overflowEpoch, _overflowEpoch + 2);
        }
        finally
        {
            _overflowGenerationLock.ExitWriteLock();
        }
    }

    private void ReleaseOverflowStorage()
    {
        if (_overflowRequestStripes is null)
        {
            return;
        }

        for (int i = 0; i < _overflowRequestStripes.Length; i++)
        {
            lock (_admissionLocks![i])
            {
                _overflowRequestStripes[i].Generations[0] = [];
                _overflowRequestStripes[i].Generations[1] = [];
            }
        }
    }

    private static OverflowRequestStripe[] CreateOverflowRequestStripes(int overflowRequestLimit)
    {
        int initialGenerationCapacity = Math.Max(1, (overflowRequestLimit + AdmissionLockCount - 1) / AdmissionLockCount);
        OverflowRequestStripe[] stripes = new OverflowRequestStripe[AdmissionLockCount];
        for (int i = 0; i < stripes.Length; i++)
        {
            stripes[i] = new OverflowRequestStripe(initialGenerationCapacity);
        }

        return stripes;
    }

    private static Lock[] CreateAdmissionLocks()
    {
        Lock[] locks = new Lock[AdmissionLockCount];
        for (int i = 0; i < locks.Length; i++)
        {
            locks[i] = new Lock();
        }

        return locks;
    }

    private bool TryEnterOperation()
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return false;
        }

        Interlocked.Increment(ref _activeOperations);
        if (Volatile.Read(ref _disposeState) == 0)
        {
            return true;
        }

        Interlocked.Decrement(ref _activeOperations);
        return false;
    }

    private void ExitOperation() => Interlocked.Decrement(ref _activeOperations);

    private bool TryRemove(TResourceId resourceId, RetryRequestEntry entry)
    {
        if (_retryRequests.TryRemove(new KeyValuePair<TResourceId, RetryRequestEntry>(resourceId, entry)))
        {
            Interlocked.Decrement(ref _trackedRequestsCounter);
            Return(entry);
            return true;
        }

        return false;
    }

    private void Return(RetryRequestEntry entry)
    {
        ReleaseHandlerSlot(entry.SourceHandler);
        HandlerSlotReleaser releaser = new(this);
        if (entry.Handlers.Deactivate(entry.Generation, ref releaser))
        {
            _handlerBagsPool.Return(entry.Handlers);
        }
    }

    private struct HandlerSlotReleaser(RetryCache<TMessage, TResourceId> cache) : IHandlerBagProcessor<TMessage>
    {
        public void Process(IMessageHandler<TMessage> handler) => cache.ReleaseHandlerSlot(handler);
    }

    internal readonly struct BatchedHandlerPreference(
        Dictionary<IBatchMessageHandler<TMessage, TResourceId>, List<TResourceId>>? batchedRetryRequests,
        int maxPreferredResourcesPerHandler)
        : IHandlerBagPreference<TMessage>
    {
        public HandlerPreference GetPreference(IMessageHandler<TMessage> handler)
        {
            if (batchedRetryRequests is null
                || handler is not IBatchMessageHandler<TMessage, TResourceId> batchHandler
                || !batchedRetryRequests.TryGetValue(batchHandler, out List<TResourceId>? resources))
            {
                return HandlerPreference.Neutral;
            }

            return resources.Count < maxPreferredResourcesPerHandler
                ? HandlerPreference.Preferred
                : HandlerPreference.Neutral;
        }
    }

    private void ReturnBatchedRetryRequests(RetryBatches? batchedRetryRequests)
    {
        if (batchedRetryRequests is not null && batchedRetryRequests.Reset())
        {
            Interlocked.CompareExchange(ref _batchedRetryRequests, batchedRetryRequests, null);
        }
    }

}

public enum AnnounceResult
{
    RequestRequired,
    Delayed
}

/// <summary>
/// Poolable handler collection with generation-guarded lifecycle.
/// <para>
/// Bags start inactive in the pool. <see cref="Activate"/> begins a new generation,
/// and every operation must present that generation. This prevents a stale reference
/// from a previous dictionary entry from mutating a pooled bag after reuse.
/// </para>
/// <para>
/// Each distinct handler can be selected once, and the total number accepted during a
/// lifecycle is bounded. Late announcements can therefore improve recovery without
/// extending a retry cycle indefinitely.
/// </para>
/// </summary>
internal sealed class HandlerBag<TMessage>
{
    private IMessageHandler<TMessage>[] _handlers = [];
    private int _handlerCount;
    private readonly Lock _lock = new();
    private bool _active;
    private int _pendingCount;
    private long _generation;

    /// <summary>
    /// Activates the bag for a new lifecycle and returns its generation.
    /// </summary>
    public long Activate()
    {
        lock (_lock)
        {
            _active = true;
            _pendingCount = 0;
            return ++_generation;
        }
    }

    /// <summary>
    /// Deactivates the matching lifecycle before returning the bag to the pool.
    /// </summary>
    public bool Deactivate<TProcessor>(long generation, ref TProcessor processor)
        where TProcessor : struct, IHandlerBagProcessor<TMessage>
    {
        lock (_lock)
        {
            if (generation != _generation)
            {
                return false;
            }

            _active = false;
            for (int i = 0; i < _pendingCount; i++)
            {
                processor.Process(_handlers[i]);
            }

            Array.Clear(_handlers, 0, _handlerCount);
            _handlerCount = 0;
            _pendingCount = 0;
            return true;
        }
    }

    /// <summary>
    /// Adds a distinct handler while the matching lifecycle is accepting candidates.
    /// </summary>
    public HandlerBagAddResult Add(IMessageHandler<TMessage> handler, int maxCount, long generation)
    {
        lock (_lock)
        {
            if (!_active || generation != _generation)
                return HandlerBagAddResult.Inactive;

            ReadOnlySpan<IMessageHandler<TMessage>> handlers = new(_handlers, 0, _handlerCount);
            if (handlers.Length >= maxCount)
                return HandlerBagAddResult.Full;

            for (int i = 0; i < handlers.Length; i++)
            {
                if (ReferenceEquals(handlers[i], handler))
                {
                    return HandlerBagAddResult.Duplicate;
                }
            }

            if (_handlerCount == _handlers.Length)
            {
                Array.Resize(ref _handlers, Math.Min(maxCount, Math.Max(4, _handlerCount * 2)));
            }

            _handlers[_handlerCount] = _handlers[_pendingCount];
            _handlers[_pendingCount] = handler;
            _handlerCount++;

            _pendingCount++;
            return HandlerBagAddResult.Added;
        }
    }

    /// <summary>
    /// Selects one pending retry handler from the matching lifecycle.
    /// </summary>
    public bool TryTake(long generation, out IMessageHandler<TMessage>? handler, out bool hasMoreHandlers)
    {
        NoHandlerPreference<TMessage> preference = default;
        return TryTake(generation, ref preference, out handler, out hasMoreHandlers);
    }

    /// <summary>
    /// Selects one pending retry handler, preferring handlers selected for related work.
    /// </summary>
    public bool TryTake<TPreference>(
        long generation,
        ref TPreference preference,
        out IMessageHandler<TMessage>? handler,
        out bool hasMoreHandlers)
        where TPreference : struct, IHandlerBagPreference<TMessage>
    {
        lock (_lock)
        {
            handler = null;
            hasMoreHandlers = false;

            if (!_active || generation != _generation)
            {
                return false;
            }

            if (_pendingCount == 0)
            {
                _active = false;
                return false;
            }

            int preferredIndex = -1;
            int preferredCount = 0;
            int neutralIndex = -1;
            int neutralCount = 0;
            for (int i = 0; i < _pendingCount; i++)
            {
                switch (preference.GetPreference(_handlers[i]))
                {
                    case HandlerPreference.Preferred:
                        if (++preferredCount == 1 || Random.Shared.Next(preferredCount) == 0)
                        {
                            preferredIndex = i;
                        }
                        break;
                    case HandlerPreference.Neutral:
                        if (++neutralCount == 1 || Random.Shared.Next(neutralCount) == 0)
                        {
                            neutralIndex = i;
                        }
                        break;
                }
            }

            int index = preferredIndex >= 0
                ? preferredIndex
                : neutralIndex >= 0
                    ? neutralIndex
                    : _pendingCount == 1 ? 0 : Random.Shared.Next(_pendingCount);

            handler = _handlers[index];
            _pendingCount--;
            (_handlers[index], _handlers[_pendingCount]) = (_handlers[_pendingCount], _handlers[index]);
            hasMoreHandlers = _pendingCount > 0;
            return true;
        }
    }

    /// <summary>
    /// Called by the pool on Return. Clears any late additions.
    /// Does NOT reactivate — only Activate() does.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _active = false;
            _pendingCount = 0;
            Array.Clear(_handlers, 0, _handlerCount);
            _handlerCount = 0;
        }
    }
}

internal enum HandlerBagAddResult
{
    Added,
    Duplicate,
    Full,
    Inactive
}

internal interface IHandlerBagProcessor<TMessage>
{
    void Process(IMessageHandler<TMessage> handler);
}

internal interface IHandlerBagPreference<TMessage>
{
    HandlerPreference GetPreference(IMessageHandler<TMessage> handler);
}

file readonly struct NoHandlerPreference<TMessage> : IHandlerBagPreference<TMessage>
{
    public HandlerPreference GetPreference(IMessageHandler<TMessage> handler) => HandlerPreference.Neutral;
}

internal enum HandlerPreference
{
    Neutral,
    Preferred
}

file sealed class HandlerBagPolicy<TMessage> : IPooledObjectPolicy<HandlerBag<TMessage>>
{
    public HandlerBag<TMessage> Create() => new();

    public bool Return(HandlerBag<TMessage> obj)
    {
        obj.Reset();
        return true;
    }
}
