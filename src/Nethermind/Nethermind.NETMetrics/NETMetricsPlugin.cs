//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//

using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Logging;
using Nethermind.Monitoring.Config;

namespace Nethermind.NETMetrics;

public class NETMetricsPlugin : INethermindPlugin

{
    public ValueTask DisposeAsync()
    {
        _metricsListener.Dispose();
        return ValueTask.CompletedTask;
    }

    private SystemMetricsListener _metricsListener = null!;
    private INethermindApi _nethermindApi = null!;
    private ILogger? _logger;
    public string Name => ".NET Performance Monitoring Plugin";
    public string Description => "Enhance .NET-related monitoring with more counters";
    public string Author => "Nethermind";
    public Task Init(INethermindApi nethermindApi)
    {
        if (!nethermindApi.Config<IMetricsConfig>().EnableDotNetMetrics) return Task.CompletedTask;

        Dictionary<string, HashSet<string>> enabledEvents = new();

        // For event counters, refer https://learn.microsoft.com/en-us/dotnet/core/diagnostics/available-counters
        // Note: For event counters, the payload name will have `-` replaced with `_` in prometheus.
        enabledEvents["EventCounters"] = new HashSet<string>()
        {
            "cpu-usage", "working-set", "gc-heap-size", "gen-0-gc-count", "gen-1-gc-count", "gen-2-gc-count",
            "threadpool-thread-count", "monitor-lock-contention-count", "threadpool-queue-length",
            "threadpool-completed-items-count", "alloc-rate", "active-timer-count", "gc-fragmentation",
            "gc-committed", "exception-count", "time-in-gc", "gen-0-size", "gen-1-size", "gen-2-size", "loh-size",
            "poh-size", "assembly-count", "il-bytes-jitted", "methods-jitted-count", "time-in-jit"
        };

        // For everything else, refer https://learn.microsoft.com/en-us/dotnet/fundamentals/diagnostics/runtime-garbage-collection-events
        enabledEvents["IncreaseMemoryPressure"] = new HashSet<string>() { "BytesAllocated", "ClrInstanceID" };
        enabledEvents["GCTriggered"] = new HashSet<string>() { "Reason", "ClrInstanceID" };
        enabledEvents["GCMarkWithType"] = new HashSet<string>() { "HeapNum", "ClrInstanceID", "Type", "Bytes" };

        // Does not make sense as metric. Maybe log instead.
        // enabledEvents["PinObjectAtGCTime"] = new HashSet<string>() { "HandleID", "ObjectID", "ObjectSize", "TypeName", "ClrInstanceID" };

        enabledEvents["GCGlobalHeapHistory_V4"] = new HashSet<string>()
        {
            "FinalYoungestDesired", "NumHeaps", "CondemnedGeneration", "Gen0ReductionCount", "Reason",
            "GlobalMechanisms", "ClrInstanceID", "PauseMode", "MemoryPressure", "CondemnReasons0",
            "CondemnReasons1", "Count"
        };
        enabledEvents["GCPerHeapHistory_V3"] = new HashSet<string>()
        {
            "ClrInstanceID", "FreeListAllocated", "FreeListRejected", "EndOfSegAllocated", "CondemnedAllocated",
            "PinnedAllocated", "PinnedAllocatedAdvance", "RunningFreeListEfficiency", "CondemnReasons0",
            "CondemnReasons1", "CompactMechanisms", "ExpandMechanisms", "HeapIndex", "ExtraGen0Commit", "Count"
        };
        enabledEvents["GCHeapStats_V2"] = new HashSet<string>()
        {
            "GenerationSize0", "TotalPromotedSize0", "GenerationSize1", "TotalPromotedSize1", "GenerationSize2",
            "TotalPromotedSize2", "GenerationSize3", "TotalPromotedSize3", "FinalizationPromotedSize",
            "FinalizationPromotedCount", "PinnedObjectCount", "SinkBlockCount", "GCHandleCount", "ClrInstanceID",
            "GenerationSize4", "TotalPromotedSize4"
        };
        _nethermindApi = nethermindApi ?? throw new ArgumentNullException(nameof(nethermindApi));
        _metricsListener = new SystemMetricsListener(enabledEvents);
        _logger = _nethermindApi.LogManager.GetClassLogger();
        return Task.CompletedTask;
    }

    public Task InitNetworkProtocol()
    {
        return Task.CompletedTask;
    }

    public Task InitRpcModules()
    {
        return Task.CompletedTask;
    }
}
