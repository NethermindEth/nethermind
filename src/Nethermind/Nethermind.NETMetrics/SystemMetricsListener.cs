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
    private Dictionary<string, string[]>EnabledEvents;

    private const int GC_KEYWORD =                 0x0000001;
    private const int TYPE_KEYWORD =               0x0080000;
    private const int GCHEAPANDTYPENAMES_KEYWORD = 0x1000000;

    public SystemMetricsListener(Dictionary<string, string[]> enabledEvents)
    {
        EnabledEvents = enabledEvents;
    }

    private const int TimeInterval = 1;

    protected override void OnEventSourceCreated(EventSource source)
    {
        Console.WriteLine($"{source.Guid} | {source.Name}");

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

    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (!EnabledEvents.Keys.Contains(eventData.EventName))
        {
            return;
        }

        for (int i = 0; i < eventData.Payload.Count; ++ i)
        {
            string? eventName;
            string? payloadName = null;
            double payloadValue = 0;

            eventName = eventData.EventName;
            if (eventName.Equals("EventCounters"))
            {
                if (eventData.Payload[i] is IDictionary<string, object> eventPayload)
                {
                    (payloadName, payloadValue) = ParseSystemMetrics(eventPayload);
                }
            }
            else
            {
                payloadName = eventData.PayloadNames[i];
                payloadValue = double.Parse(eventData.Payload[i].ToString());
            }

            if (payloadName is null || !EnabledEvents[eventName].Contains(payloadName))
            {
                return;
            }

            Metrics.RuntimeMetrics[eventName + "_" + payloadName] = payloadValue;
        }
    }

    private static (string? payloadName, double payloadValue) ParseSystemMetrics(
        IDictionary<string, object> eventPayload)
    {
        string payloadName = "";

        if (eventPayload.TryGetValue("Name", out object displayValue))
        {
            payloadName = displayValue.ToString().Replace("-", "_");
        }

        if (eventPayload.TryGetValue("Mean", out object value))
        {
            return (payloadName, double.Parse(value.ToString()));
        }

        if (eventPayload.TryGetValue("Increment", out value))
        {
            return (payloadName, double.Parse(value.ToString()));
        }

        return (null, 0);
    }

}
