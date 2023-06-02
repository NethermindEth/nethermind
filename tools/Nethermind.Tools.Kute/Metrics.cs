// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute;

public class Metrics
{
    public int Messages { get; private set; }
    public int Failed { get; private set; }
    public int Ignored { get; private set; }
    public int Responses { get; private set; }
    public IDictionary<string, MethodMetrics> Requests { get; } = new Dictionary<string, MethodMetrics>();

    public TimeSpan TotalRunningTime { get; set; }

    public void TickMessages() => Messages++;
    public void TickFailed() => Failed++;
    public void TickIgnored() => Ignored++;
    public void TickResponses() => Responses++;

    public void TickMethod(string methodName, TimeSpan runningTime)
    {
        if (Requests.TryGetValue(methodName, out MethodMetrics? request))
        {
            request.Tick(runningTime);
        }
        else
        {
            Requests[methodName] = new MethodMetrics();
        }
    }

    public class MethodMetrics
    {
        public int Count { get; private set; }
        public TimeSpan RunningTime { get; private set; }

        public MethodMetrics()
        {
            Count = 0;
            RunningTime = TimeSpan.Zero;
        }

        public void Tick(TimeSpan runningTime)
        {
            Count++;
            RunningTime += runningTime;
        }
    }
}
