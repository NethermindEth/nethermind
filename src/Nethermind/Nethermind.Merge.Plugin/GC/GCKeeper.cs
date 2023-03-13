// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime;
using System.Threading.Tasks;
using FastEnumUtility;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin.GC;

public class GCKeeper
{
    private readonly IGCStrategy _igcStrategy;
    private readonly ILogger _logger;
    private static readonly long _defaultSize = 512.MB();
    private Task _gcScheduleTask = Task.CompletedTask;

    public GCKeeper(IGCStrategy igcStrategy, ILogManager logManager)
    {
        _igcStrategy = igcStrategy;
        _logger = logManager.GetClassLogger<GCKeeper>();
    }

    public IDisposable TryStartNoGCRegion(long? size = null)
    {
        size ??= _defaultSize;
        if (_igcStrategy.ShouldControlGCToReducePauses())
        {
            bool noGcRegion = System.GC.TryStartNoGCRegion(size.Value, true);
            return new NoGCRegion(noGcRegion ? FailCause.None : FailCause.GC, size, _logger);
        }

        return new NoGCRegion(FailCause.Strategy, size, _logger);
    }

    private enum FailCause
    {
        None,
        Strategy,
        GC,
    }

    private class NoGCRegion : IDisposable
    {
        private readonly FailCause _failCause;
        private readonly long? _size;
        private readonly ILogger _logger;

        internal NoGCRegion(FailCause failCause, long? size, ILogger logger)
        {
            _failCause = failCause;
            _size = size;
            _logger = logger;
        }

        public void Dispose()
        {
            if (_failCause == FailCause.None)
            {
                if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
                {
                    try
                    {
                        System.GC.EndNoGCRegion();
                    }
                    catch (InvalidOperationException)
                    {
                        if (_logger.IsDebug) _logger.Debug($"Failed to keep in NoGCRegion with Exception with {_size} bytes");
                    }
                }
                else if (_logger.IsDebug) _logger.Debug($"Failed to keep in NoGCRegion with {_size} bytes");
            }
            else if (_logger.IsDebug) _logger.Debug($"Failed to start NoGCRegion with {_size} bytes with cause {_failCause.FastToString()}");
        }
    }

    public void ScheduleGC()
    {
        if (_gcScheduleTask.IsCompleted) _gcScheduleTask = ScheduleGCInternal();
    }

    private async Task ScheduleGCInternal()
    {
        if (_igcStrategy.ShouldControlGCToReducePauses())
        {
            await Task.Delay(1000);
            System.GC.Collect(System.GC.MaxGeneration, GCCollectionMode.Default, false, true);
        }
    }
}
