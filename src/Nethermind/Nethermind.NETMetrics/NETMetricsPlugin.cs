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

        // Refer for event counters, refer https://learn.microsoft.com/en-us/dotnet/core/diagnostics/available-counters
        enabledEvents["EventCounters"] = new HashSet<string>()
        {
            "cpu_usage", "working_set", "gc_heap_size", "gen_0_gc_count", "gen_1_gc_count", "gen_2_gc_count",
            "threadpool_thread_count", "monitor_lock_contention_count", "threadpool_queue_length",
            "threadpool_completed_items_count", "alloc_rate", "active_timer_count", "gc_fragmentation",
            "gc_committed", "exception_count", "time_in_gc", "gen_0_size", "gen_1_size", "gen_2_size", "loh_size",
            "poh_size", "assembly_count", "il_bytes_jitted", "methods_jitted_count", "time_in_jit"
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
