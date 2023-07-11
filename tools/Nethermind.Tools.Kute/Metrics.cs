// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute;

public class Metrics
{
    public TimeSpan TotalRunningTime { get; set; }
    public int Messages { get; private set; }
    public int Failed { get; private set; }
    public int IgnoredRequests { get; private set; }
    public int Responses { get; private set; }
    public ItemMetrics Batches { get; } = new();
    public IDictionary<string, ItemMetrics> ProcessedRequests { get; } = new Dictionary<string, ItemMetrics>();

    public void TickMessages() => Messages++;
    public void TickFailed() => Failed++;
    public void TickIgnoredRequests() => IgnoredRequests++;
    public void TickResponses() => Responses++;
    public void TickBatch(TimeSpan runningTime) => Batches.Tick(runningTime);

    public void TickRequest(string methodName, TimeSpan runningTime)
    {
        if (!ProcessedRequests.ContainsKey(methodName))
        {
            ProcessedRequests[methodName] = new ItemMetrics();
        }

        ProcessedRequests[methodName].Tick(runningTime);
    }

    public class ItemMetrics
    {
        public int Count { get; private set; }
        public TimeSpan RunningTime { get; private set; }

        public ItemMetrics()
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
