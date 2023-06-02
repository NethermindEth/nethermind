// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute;

public class Metrics
{
    public TimeSpan TotalRunningTime { get; set; }
    public int Messages { get; private set; }
    public int Failed { get; private set; }
    public int Responses { get; private set; }
    public int IgnoredRequests { get; private set; }
    public IDictionary<string, MethodMetrics> ProcessedRequests { get; } = new Dictionary<string, MethodMetrics>();

    public void TickMessages() => Messages++;
    public void TickFailed() => Failed++;
    public void TickIgnoredRequests() => IgnoredRequests++;
    public void TickResponses() => Responses++;

    public void TickRequest(string methodName, TimeSpan runningTime)
    {
        if (!ProcessedRequests.ContainsKey(methodName))
        {
            ProcessedRequests[methodName] = new MethodMetrics();
        }

        ProcessedRequests[methodName].Tick(runningTime);
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
