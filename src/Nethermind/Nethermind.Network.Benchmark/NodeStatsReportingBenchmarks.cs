// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Benchmarks;

[MemoryDiagnoser, ThreadingDiagnoser]
public class NodeStatsReportingBenchmarks
{
    private const int PeerCount = 50;
    private const int Rounds = 256;
    private const int ReportsPerInvocation = PeerCount * Rounds;
    private readonly ConcurrentDictionary<Node, INodeStats> _baseline = new(new NodeComparer());
    private NodeStatsManager _manager = null!;
    private Node[] _nodes = null!;
    private Action<int> _baselineWorker = null!;
    private Action<int> _managerWorker = null!;

    [Params(1, 4, 8)]
    public int Workers { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _manager = new NodeStatsManager(TimerFactory.Default, LimboLogs.Instance);
        _nodes = TestItem.PublicKeys.Take(PeerCount).Select(key => new Node(key, "8.8.8.8", 30303)).ToArray();
        foreach (Node node in _nodes)
        {
            _baseline[node] = new NodeStatsLight(node);
            _manager.GetOrAdd(node);
        }
        _baselineWorker = worker => Report(worker, false);
        _managerWorker = worker => Report(worker, true);
    }

    [GlobalCleanup]
    public void Cleanup() => _manager.Dispose();

    [Benchmark(Baseline = true, OperationsPerInvoke = ReportsPerInvocation)]
    public void ConcurrentDictionaryBaseline() => Parallel.For(0, Workers, _baselineWorker);

    [Benchmark(OperationsPerInvoke = ReportsPerInvocation)]
    public void BoundedManager() => Parallel.For(0, Workers, _managerWorker);

    private void Report(int worker, bool bounded)
    {
        for (int round = 0; round < Rounds / Workers; round++)
        {
            for (int i = 0; i < PeerCount; i++)
            {
                Node node = _nodes[(i + worker * 7) % PeerCount];
                if (bounded) _manager.ReportEvent(node, NodeStatsEventType.P2PPingIn);
                else
                {
                    if (!_baseline.TryGetValue(node, out INodeStats? stats))
                        stats = _baseline.GetOrAdd(node, static key => new NodeStatsLight(key));
                    stats.AddNodeStatsEvent(NodeStatsEventType.P2PPingIn);
                }
            }
        }
    }

    private sealed class NodeComparer : IEqualityComparer<Node>
    {
        public bool Equals(Node? x, Node? y) => ReferenceEquals(x, y) || (x is not null && y is not null && x.Id == y.Id);
        public int GetHashCode(Node obj) => obj.GetHashCode();
    }
}
