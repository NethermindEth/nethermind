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

using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;


namespace Nethermind.NETMetrics;

public class SystemMetricsListener: EventListener
{
    public SystemMetricsListener(int timeInterval)
    {
        _timeInterval = timeInterval;
    }

    private readonly int _timeInterval;

    protected override void OnEventSourceCreated(EventSource source)
    {
        // Console.WriteLine($"{source.Guid} | {source.Name}");

        if (!source.Name.Equals("System.Runtime"))
        {
            return;
        }

        EnableEvents(source, EventLevel.Verbose, EventKeywords.All, new Dictionary<string, string?>()
            {
                ["EventCounterIntervalSec"] = _timeInterval.ToString()
            }
        );
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {

        if (!eventData.EventName.Equals("EventCounters"))
        {
            return;
        }

        for (int i = 0; i < eventData.Payload.Count; ++ i)
        {
            if (eventData.Payload[i] is IDictionary<string, object> eventPayload)
            {
                var (counterName, counterValue) = GetRelevantMetric(eventPayload);
                UpdateMetric(counterName, counterValue);
            }
        }
    }

    private static void UpdateMetric(string counterName, string counterValue)
    {
        long value = long.Parse(counterValue);
        switch (counterName)
        {
            case "time-in-gc":
                Metrics.TimeInGcSinceLastGc = value;
                break;
            case "alloc-rate":
                Metrics.AllocationRate = value;
                break;
            case "gc-committed":
                Metrics.GcCommittedBytes = value;
                break;
            case "gc-fragmentation":
                Metrics.GcFragmentation = value;
                break;
            case "gc-heap-size":
                Metrics.GcHeapSize= value;
                break;
            case "gen-0-gc-count":
                Metrics.Gen0GcCount = value;
                break;
            case "gen-0-size":
                Metrics.Gen0Size = value;
                break;
            case "gen-1-gc-count":
                Metrics.Gen1GcCount = value;
                break;
            case "gen-1-size":
                Metrics.Gen1Size = value;
                break;
            case "gen-2-gc-count":
                Metrics.Gen2GcCount = value;
                break;
            case "gen-2-size":
                Metrics.Gen2Size = value;
                break;
            case "loh-size":
                Metrics.LohSize = value;
                break;
            case "poh-size":
                Metrics.PohSize = value;
                break;
            case "cpu-usage":
                Metrics.CpuUsage = value;
                break;
        }
    }

    private static (string counterName, string counterValue) GetRelevantMetric(
        IDictionary<string, object> eventPayload)
    {
        var counterName = "";
        var counterValue = "";

        if (eventPayload.TryGetValue("Name", out object displayValue))
        {
            counterName = displayValue.ToString();
        }
        if (eventPayload.TryGetValue("Mean", out object value) ||
            eventPayload.TryGetValue("Increment", out value))
        {
            counterValue = value.ToString();
        }

        return (counterName, counterValue);
    }
}
