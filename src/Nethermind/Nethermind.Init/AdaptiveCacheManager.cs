// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core.Caching;
using Nethermind.Logging;

namespace Nethermind.Init;

internal sealed class AdaptiveCacheManager : IAdaptiveCacheManager, IDisposable
{
    // Non-cache allocations retain 75% of the detected process limit. The two memory-pressure
    // thresholds provide hysteresis so a cache is not grown immediately after pressure subsides.
    private const int CacheBudgetPercent = 25;
    private const int HighMemoryPressurePercent = 75;
    private const int LowMemoryPressurePercent = 60;
    private const int PressureShrinkPercent = 75;
    private const int SaturatedPercent = 85;
    private static readonly TimeSpan RebalanceInterval = TimeSpan.FromSeconds(10);

    private readonly Lock _lock = new();
    private readonly List<IAdaptiveCache> _caches = [];
    private readonly ILogger _logger;
    private readonly long _memoryLimit;
    private readonly long _cacheBudget;
    private readonly CancellationTokenSource? _cancellationTokenSource;
    private readonly Task _rebalanceTask = Task.CompletedTask;
    private bool _disposed;

    public AdaptiveCacheManager(IInitConfig initConfig, IProcessExitSource processExitSource, ILogManager logManager)
    {
        _logger = logManager.GetClassLogger<AdaptiveCacheManager>();
        _memoryLimit = GetMemoryLimit(initConfig.AdaptiveCacheMemoryLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_memoryLimit);
        _cacheBudget = Percentage(_memoryLimit, CacheBudgetPercent);

        if (initConfig.AdaptiveCacheEnabled)
        {
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(processExitSource.Token);
            _rebalanceTask = RunAsync(_cancellationTokenSource.Token);
            if (_logger.IsInfo)
                _logger.Info($"Adaptive cache enabled with {_cacheBudget / 1_000_000:N0} MB cache budget and {_memoryLimit / 1_000_000:N0} MB process memory limit");
        }
    }

    internal AdaptiveCacheManager(long memoryLimit, ILogManager logManager)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(memoryLimit);
        _logger = logManager.GetClassLogger<AdaptiveCacheManager>();
        _memoryLimit = memoryLimit;
        _cacheBudget = Percentage(memoryLimit, CacheBudgetPercent);
    }

    public void Register(IAdaptiveCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _caches.Add(cache);
        }
    }

    internal void Rebalance(long workingSet)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(workingSet);

        lock (_lock)
        {
            if (_disposed || _caches.Count == 0) return;

            long totalCapacity = GetTotalCapacity();
            if (workingSet >= Percentage(_memoryLimit, HighMemoryPressurePercent))
            {
                ShrinkAll(PressureShrinkPercent);
                return;
            }

            if (totalCapacity > _cacheBudget)
            {
                TrimToBudget(totalCapacity - _cacheBudget);
                totalCapacity = GetTotalCapacity();
            }

            if (workingSet <= Percentage(_memoryLimit, LowMemoryPressurePercent))
            {
                GrowMostConstrained(totalCapacity);
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        if (_cancellationTokenSource is not null)
        {
            _cancellationTokenSource.Cancel();
            try
            {
                _rebalanceTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
            _cancellationTokenSource.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(RebalanceInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                Rebalance(Environment.WorkingSet);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (_logger.IsError) _logger.Error("Adaptive cache controller stopped", exception);
        }
    }

    private void ShrinkAll(int targetPercent)
    {
        for (int i = 0; i < _caches.Count; i++)
        {
            IAdaptiveCache cache = _caches[i];
            long target = Math.Max(cache.MinimumCapacity, Percentage(cache.Capacity, targetPercent));
            Resize(cache, target, "memory pressure");
        }
    }

    private void TrimToBudget(long bytesToRelease)
    {
        while (bytesToRelease > 0)
        {
            IAdaptiveCache? candidate = null;
            double lowestUtilization = double.MaxValue;

            for (int i = 0; i < _caches.Count; i++)
            {
                IAdaptiveCache cache = _caches[i];
                if (cache.Capacity <= cache.MinimumCapacity) continue;

                double utilization = cache.Capacity == 0 ? 0 : (double)cache.Usage / cache.Capacity;
                if (utilization < lowestUtilization)
                {
                    candidate = cache;
                    lowestUtilization = utilization;
                }
            }

            if (candidate is null) return;

            long capacityBefore = candidate.Capacity;
            long usageHeadroom = candidate.Usage + candidate.Usage / 4;
            long target = Math.Max(candidate.MinimumCapacity, Math.Max(usageHeadroom, capacityBefore - bytesToRelease));
            if (target >= capacityBefore)
            {
                target = Math.Max(candidate.MinimumCapacity, capacityBefore - Math.Max(1, bytesToRelease));
            }

            Resize(candidate, target, "cache budget");
            long released = capacityBefore - candidate.Capacity;
            if (released <= 0) return;
            bytesToRelease -= released;
        }
    }

    private void GrowMostConstrained(long totalCapacity)
    {
        long available = _cacheBudget - totalCapacity;
        if (available <= 0) return;

        IAdaptiveCache? candidate = null;
        double highestUtilization = (double)SaturatedPercent / 100;
        for (int i = 0; i < _caches.Count; i++)
        {
            IAdaptiveCache cache = _caches[i];
            if (cache.Capacity >= cache.MaximumCapacity || cache.Capacity == 0) continue;

            double utilization = (double)cache.Usage / cache.Capacity;
            if (utilization >= highestUtilization)
            {
                candidate = cache;
                highestUtilization = utilization;
            }
        }

        if (candidate is null) return;

        long growth = Math.Max(candidate.MinimumCapacity, candidate.Capacity / 4);
        long target = Math.Min(candidate.MaximumCapacity, candidate.Capacity + Math.Min(growth, available));
        Resize(candidate, target, "cache demand");
    }

    private long GetTotalCapacity()
    {
        long total = 0;
        for (int i = 0; i < _caches.Count; i++)
        {
            total = checked(total + _caches[i].Capacity);
        }
        return total;
    }

    private void Resize(IAdaptiveCache cache, long target, string reason)
    {
        target = Math.Clamp(target, cache.MinimumCapacity, cache.MaximumCapacity);
        long previous = cache.Capacity;
        if (target == previous) return;

        cache.SetCapacity(target);
        if (_logger.IsInfo)
            _logger.Info($"Adaptive cache resized {cache.Name} from {previous / 1_000_000:N0} MB to {target / 1_000_000:N0} MB ({reason}, usage {cache.Usage / 1_000_000:N0} MB)");
    }

    private static long GetMemoryLimit(ulong? configuredLimit)
    {
        long detected = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (configuredLimit is null) return detected;
        return (long)Math.Min(configuredLimit.Value, (ulong)long.MaxValue);
    }

    private static long Percentage(long value, int percent) => checked(value / 100 * percent);
}
