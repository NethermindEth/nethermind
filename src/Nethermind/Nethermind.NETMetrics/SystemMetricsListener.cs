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
using System.Collections.ObjectModel;
using System.Diagnostics.Tracing;
using System.Reflection;


namespace Nethermind.NETMetrics;

public class SystemMetricsListener : EventListener
{
    private string[] EnabledEvents = new[] {"System.Runtime", "Microsoft-Windows-DotNETRuntime"};

    private const int GC_KEYWORD =                 0x0000001;
    private const int TYPE_KEYWORD =               0x0080000;
    private const int GCHEAPANDTYPENAMES_KEYWORD = 0x1000000;

    public SystemMetricsListener()
    {
    }

    private const int TimeInterval = 1;

    protected override void OnEventSourceCreated(EventSource source)
    {
        Console.WriteLine($"{source.Guid} | {source.Name}");

        if (!EnabledEvents.Contains(source.Name))
        {
            return;
        }

        if (source.Name.Equals("System.Runtime"))
        {
            EnableEvents(source, EventLevel.Verbose, EventKeywords.All, new Dictionary<string, string?>()
                {
                    ["EventCounterIntervalSec"] = "1"
                }
            );
        }

        if (source.Name.Equals("Microsoft-Windows-DotNETRuntime"))
        {
            EnableEvents(
                source,
                EventLevel.Verbose,
                (EventKeywords) (GC_KEYWORD | GCHEAPANDTYPENAMES_KEYWORD | TYPE_KEYWORD),
                new Dictionary<string, string?>()
                {
                    ["EventCounterIntervalSec"] = TimeInterval.ToString()
                }

            );
        }

        if (source.Name.Equals("System.Runtime"))
        {
            EnableEvents(source, EventLevel.Verbose, EventKeywords.All, new Dictionary<string, string?>()
                {
                    ["EventCounterIntervalSec"] = TimeInterval.ToString()
                }
            );
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (!EnabledEvents.Contains(eventData.EventName))
        {
            return;
        }

        for (int i = 0; i < eventData.Payload.Count; ++ i)
        {
            if (eventData.EventName.Equals("System.Runtime"))
            {
                if (eventData.Payload[i] is IDictionary<string, object> eventPayload)
                {
                    UpdateSystemMetrics(eventPayload);
                }
            }

            if (eventData.EventName.Equals("Microsoft-Windows-DotNETRuntime"))
            {
                UpdateDotNETMetrics(eventData.EventName, eventData.PayloadNames[i], eventData.Payload[i].ToString());
            }

        }
    }

    private static void UpdateSystemMetrics(
        IDictionary<string, object> eventPayload)
    {
        string counterName = "";

        if (eventPayload.TryGetValue("Name", out object displayValue))
        {
            counterName = displayValue.ToString().Replace("-", "_");
        }

        if (eventPayload.TryGetValue("Mean", out object value))
        {
            Metrics.SystemRuntimeMetric[counterName] = double.Parse(value.ToString());
            return;
        }

        if (eventPayload.TryGetValue("Increment", out value))
        {
            Metrics.SystemRuntimeMetric[counterName] = double.Parse(value.ToString());
            return;
        }

    }

    private static void UpdateDotNETMetrics(string name, string payloadName, string payloadValue)
    {
        Metrics.DotNETRuntimeMetric[name + payloadName] = double.Parse(payloadValue);
    }

}
