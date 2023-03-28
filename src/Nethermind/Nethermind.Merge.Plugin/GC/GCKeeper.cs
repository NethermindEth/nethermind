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
    private readonly IGCStrategy _gcStrategy;
    private readonly ILogger _logger;
    private static readonly long _defaultSize = 512.MB();
    private Task _gcScheduleTask = Task.CompletedTask;

    public GCKeeper(IGCStrategy gcStrategy, ILogManager logManager)
    {
        _gcStrategy = gcStrategy;
        _logger = logManager.GetClassLogger<GCKeeper>();
    }

    public IDisposable TryStartNoGCRegion(long? size = null)
    {
        size ??= _defaultSize;
        var priorLatencyMode = System.Runtime.GCSettings.LatencyMode;
        //if (_gcStrategy.CanStartNoGCRegion())
        if (priorLatencyMode != GCLatencyMode.SustainedLowLatency)
        {
            FailCause failCause = FailCause.None;
            try
            {
                System.Runtime.GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
                //if (!System.GC.TryStartNoGCRegion(size.Value, true))
                //{
                //    failCause = FailCause.GCFailedToStartNoGCRegion;
                //}
            }
            catch (ArgumentOutOfRangeException)
            {
                failCause = FailCause.TotalSizeExceededTheEphemeralSegmentSize;
            }
            catch (InvalidOperationException)
            {
                failCause = FailCause.AlreadyInNoGCRegion;
            }
            catch (Exception e)
            {
                failCause = FailCause.Exception;

                if (_logger.IsError) _logger.Error($"{nameof(System.GC.TryStartNoGCRegion)} failed with exception.", e);
            }

            return new NoGCRegion(this, priorLatencyMode, failCause, size, _logger);
        }

        return new NoGCRegion(this, priorLatencyMode, FailCause.StrategyDisallowed, size, _logger);
    }

    private enum FailCause
    {
        None,
        StrategyDisallowed,
        GCFailedToStartNoGCRegion,
        TotalSizeExceededTheEphemeralSegmentSize,
        AlreadyInNoGCRegion,
        Exception
    }

    private class NoGCRegion : IDisposable
    {
        private readonly GCKeeper _gcKeeper;
        private readonly GCLatencyMode _priorMode;
        private readonly FailCause _failCause;
        private readonly long? _size;
        private readonly ILogger _logger;

        internal NoGCRegion(GCKeeper gcKeeper, GCLatencyMode priorMode, FailCause failCause, long? size, ILogger logger)
        {
            _gcKeeper = gcKeeper;
            _priorMode = priorMode;
            _failCause = failCause;
            _size = size;
            _logger = logger;
        }

        public void Dispose()
        {
            if (_failCause == FailCause.None)
            {
                //if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
                if (GCSettings.LatencyMode == GCLatencyMode.SustainedLowLatency &&
                    _priorMode != GCLatencyMode.SustainedLowLatency)
                {
                    try
                    {
                        GCSettings.LatencyMode = _priorMode;
                        //System.GC.EndNoGCRegion();
                        _gcKeeper.ScheduleGC();
                    }
                    catch (InvalidOperationException)
                    {
                        if (_logger.IsDebug) _logger.Debug($"Failed to keep in NoGCRegion with Exception with {_size} bytes");
                    }
                    catch (Exception e)
                    {
                        if (_logger.IsError) _logger.Error($"{nameof(System.GC.EndNoGCRegion)} failed with exception.", e);
                    }
                }
                else if (_logger.IsDebug) _logger.Debug($"Failed to keep in NoGCRegion with {_size} bytes");
            }
            else if (_logger.IsDebug) _logger.Debug($"Failed to start NoGCRegion with {_size} bytes with cause {_failCause.FastToString()}");
        }
    }

    private void ScheduleGC()
    {
        if (_gcScheduleTask.IsCompleted)
        {
            lock (_gcStrategy)
            {
                if (_gcScheduleTask.IsCompleted)
                {
                    _gcScheduleTask = ScheduleGCInternal();
                }
            }
        }
    }

    private async Task ScheduleGCInternal()
    {
        (GcLevel generation, GcCompaction compacting) = _gcStrategy.GetForcedGCParams();
        if (generation > GcLevel.NoGC)
        {
            // This should give time to finalize response in Engine API
            // Normally we should get block every 12s (5s on some chains)
            // Lets say we process block in 2s, then delay 1s, then invoke GC
            await Task.Delay(100);
            if (_logger.IsDebug) _logger.Debug($"Forcing GC collection of gen {generation}, compacting {compacting}");
            if (generation == GcLevel.Gen2 && compacting == GcCompaction.Full)
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            }

            if (GCSettings.LatencyMode != GCLatencyMode.NoGCRegion)
            {
                System.GC.Collect((int)generation, GCCollectionMode.Forced, blocking: true, compacting: compacting > 0);
            }
        }
    }
}
