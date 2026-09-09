// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.State.Flat.History.Test.Archive;

namespace Nethermind.JsonRpc.Benchmark
{
    /// <summary>
    /// Measures <c>eth_call</c> at a historical block against a real on-disk chain, so the archive-index read path
    /// is timed with everything above it in place — the RPC module, the blockchain bridge, the historical world
    /// state scope and the EVM.
    /// </summary>
    /// <remarks>
    /// The first run generates the chain, which takes minutes. Later runs — including the separate process
    /// BenchmarkDotNet starts per parameter combination — reuse the directory. Delete it to rebuild.
    /// </remarks>
    [MemoryDiagnoser]
    public class ArchiveEthCallBenchmarks
    {
        // moves the read window far between invocations
        private const int WindowStride = 7919;

        private ArchiveChainFixture _fixture = null!;
        private CallWorkers _workers = null!;
        private int _invocation;
        private ulong _targetBlock;
        private int _windowRange;

        [Params(1, 4)]
        public int Threads { get; set; }

        [Params(8, 200)]
        public int SlotsPerCall { get; set; }

        [GlobalSetup]
        public async Task GlobalSetup()
        {
            _fixture = new ArchiveChainFixture(ArchiveChainShape.FromEnvironment());
            await _fixture.BuildAsync();

            _windowRange = _fixture.Shape.TotalSlots - SlotsPerCall;
            if (_windowRange <= 0)
            {
                throw new InvalidOperationException(
                    $"{nameof(SlotsPerCall)}={SlotsPerCall} does not fit in {_fixture.Shape.TotalSlots} slots.");
            }

            _workers = new CallWorkers(Threads, RunOneCall);

            // Fail here rather than reporting the timing of an error response.
            _targetBlock = _fixture.QueryBlock;
            RunOneCall(0);
        }

        [GlobalCleanup]
        public void GlobalCleanup()
        {
            _workers?.Dispose();
            _fixture?.Dispose();
        }

        /// <summary>The measured case: the state comes from the history index.</summary>
        [Benchmark]
        public void CallAtHistoricalBlock()
        {
            _targetBlock = _fixture.QueryBlock;
            _workers.RunOnce();
        }

        /// <summary>
        /// The same call against the head state, which never touches the history index. It carries the fixed cost
        /// of the RPC and EVM layers, so the gap between the two is what the archive-index read actually costs.
        /// </summary>
        [Benchmark(Baseline = true)]
        public void CallAtHead()
        {
            _targetBlock = _fixture.HeadBlock;
            _workers.RunOnce();
        }

        private void RunOneCall(int workerIndex)
        {
            long invocation = (uint)Interlocked.Increment(ref _invocation);
            ulong firstSlot = (ulong)(invocation * WindowStride % _windowRange);
            ResultWrapper<HexBytes> result = _fixture.Call(firstSlot, SlotsPerCall, _targetBlock);

            if (result.Result.ResultType != ResultType.Success)
            {
                throw new InvalidOperationException($"eth_call at block {_targetBlock} failed: {result.Result.Error}");
            }
        }

        /// <summary>
        /// Runs one call per worker on dedicated threads, released together and joined before the operation ends.
        /// </summary>
        /// <remarks>
        /// Dedicated threads rather than the thread pool: the RocksDB seek iterators under test are pooled per
        /// thread and refreshed on a timer, so pool threads would keep handing the benchmark a cold pool and hide
        /// exactly the contention this parameter exists to expose. The release/join handshake costs the same at
        /// every thread count, so it does not distort the comparison between them.
        /// </remarks>
        private sealed class CallWorkers : IDisposable
        {
            private readonly SemaphoreSlim[] _release;
            private readonly CountdownEvent _completed;
            private readonly Thread[] _threads;
            private readonly Action<int> _work;
            private volatile bool _stopping;

            public CallWorkers(int count, Action<int> work)
            {
                _work = work;
                _release = new SemaphoreSlim[count];
                _completed = new CountdownEvent(count);
                _threads = new Thread[count];

                for (int i = 0; i < count; i++)
                {
                    _release[i] = new SemaphoreSlim(0, 1);
                    int index = i;
                    _threads[i] = new Thread(() => Loop(index)) { IsBackground = true, Name = $"archive-call-{index}" };
                    _threads[i].Start();
                }
            }

            public void RunOnce()
            {
                _completed.Reset(_threads.Length);
                foreach (SemaphoreSlim release in _release) release.Release();
                _completed.Wait();
            }

            public void Dispose()
            {
                _stopping = true;
                RunOnce();

                foreach (Thread thread in _threads) thread.Join();
                foreach (SemaphoreSlim release in _release) release.Dispose();
                _completed.Dispose();
            }

            private void Loop(int index)
            {
                while (true)
                {
                    _release[index].Wait();

                    try
                    {
                        if (_stopping) return;
                        _work(index);
                    }
                    finally
                    {
                        _completed.Signal();
                    }
                }
            }
        }
    }
}
