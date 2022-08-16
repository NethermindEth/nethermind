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
using System.Reflection;


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
                UpdateMetrics(eventPayload);
            }
        }
    }

    private static void UpdateMetrics(
        IDictionary<string, object> eventPayload)
    {
        var counterName = "";
        var counterValue = "";

        if (eventPayload.TryGetValue("Name", out object displayValue))
        {
            counterName = displayValue.ToString();
        }
        if (eventPayload.TryGetValue("Mean", out object value))
        {
            Metrics.SystemRuntimeMetric[counterName] = long.Parse(value.ToString());
            return;
        }

        if (eventPayload.TryGetValue("Increment", out value))
        {
            Metrics.SystemRuntimeMetric[counterName] += long.Parse(value.ToString());
            return;
        }

    }

    enum MetricType
    {
        Increment,
        Mean
    }
}
