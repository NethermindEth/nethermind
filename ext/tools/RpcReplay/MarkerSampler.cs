using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.IlEvm;

namespace RpcReplay;

/// <summary>
/// Samples <see cref="IlEvmDiagnostics.Marker"/> from a thread of its own while the harness runs its requests one at a
/// time: each sample names what compiled code ran at that moment, the helpers and bodies it called counted under the
/// opcode that called them.
/// </summary>
internal sealed class MarkerSampler : IDisposable
{
    private readonly Thread _thread;
    private volatile bool _stop;
    private readonly Dictionary<int, long> _counts = [];
    private long _samples;

    public MarkerSampler()
    {
        _thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "marker sampler" };
        _thread.Start();
    }

    private void Loop()
    {
        long period = System.Diagnostics.Stopwatch.Frequency / 50_000;
        long next = System.Diagnostics.Stopwatch.GetTimestamp();
        while (!_stop)
        {
            int marker = Volatile.Read(ref IlEvmDiagnostics.Marker);
            if (marker != 0)
            {
                lock (_counts)
                    _counts[marker] = _counts.GetValueOrDefault(marker) + 1;
            }

            _samples++;
            next += period;
            while (System.Diagnostics.Stopwatch.GetTimestamp() < next)
                Thread.SpinWait(20);
        }
    }

    public void Report()
    {
        _stop = true;
        _thread.Join();
        Dictionary<int, long> counts;
        lock (_counts)
            counts = new(_counts);
        long inCompiled = counts.Values.Sum();
        Console.WriteLine($"marker samples: {_samples} taken, {inCompiled} in compiled code");
        if (inCompiled == 0)
            return;
        (string Method, string Category, int Pc) Name(int id) => IlEvmDiagnostics.MarkerNames.TryGetValue(id, out var name) ? name : ("?", "?", -1);
        Console.WriteLine("marker samples by category:");
        foreach (var group in counts.GroupBy(e => Name(e.Key).Category).Select(g => (g.Key, Count: g.Sum(e => e.Value))).OrderByDescending(g => g.Count).Take(40))
            Console.WriteLine($"  {100.0 * group.Count / inCompiled,5:F1}% {group.Count,8} {group.Key}");
        Console.WriteLine("marker samples by site:");
        foreach ((int id, long count) in counts.OrderByDescending(e => e.Value).Take(40))
        {
            (string method, string category, int pc) = Name(id);
            Console.WriteLine($"  {100.0 * count / inCompiled,5:F1}% {count,8} {method} pc {pc} {category}");
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join();
    }
}
