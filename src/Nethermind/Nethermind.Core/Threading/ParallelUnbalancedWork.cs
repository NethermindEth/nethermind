// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Core.Threading;

public class ParallelUnbalancedWork : IThreadPoolWorkItem
{
    private static readonly ParallelOptions s_parallelOptions = new()
    {
        // default to the number of processors
        MaxDegreeOfParallelism = Environment.ProcessorCount
    };

    private readonly Data _data;

    public static void For(int fromInclusive, int toExclusive, Action<int> action)
        => For(fromInclusive, toExclusive, s_parallelOptions, action);

    public static void For(int fromInclusive, int toExclusive, ParallelOptions parallelOptions, Action<int> action)
    {
        int threads = parallelOptions.MaxDegreeOfParallelism > 0 ? parallelOptions.MaxDegreeOfParallelism : Environment.ProcessorCount;

        Data data = new(threads, fromInclusive, toExclusive, action);

        for (int i = 0; i < threads - 1; i++)
        {
            ThreadPool.UnsafeQueueUserWorkItem(new ParallelUnbalancedWork(data), preferLocal: false);
        }

        new ParallelUnbalancedWork(data).Execute();

        if (data.ActiveThreads > 0)
        {
            lock (data)
            {
                if (data.ActiveThreads > 0)
                {
                    // Wait for remaining to complete
                    Monitor.Wait(data);
                }
            }
        }
    }

    public static void For<TLocal>(
        int fromInclusive,
        int toExclusive,
        ParallelOptions parallelOptions,
        Func<TLocal> init,
        Func<int, TLocal, TLocal> action,
        Action<TLocal> @finally)
        => InitProcessor<TLocal>.For(fromInclusive, toExclusive, parallelOptions, init, default, action, @finally);

    public static void For<TLocal>(int fromInclusive, int toExclusive, TLocal state, Func<int, TLocal, TLocal> action)
        => For(fromInclusive, toExclusive, s_parallelOptions, state, action);

    public static void For<TLocal>(
        int fromInclusive,
        int toExclusive,
        ParallelOptions parallelOptions,
        TLocal state,
        Func<int, TLocal, TLocal> action)
        => InitProcessor<TLocal>.For(fromInclusive, toExclusive, parallelOptions, null, state, action);

    private ParallelUnbalancedWork(Data data)
    {
        _data = data;
    }

    public void Execute()
    {
        int i = _data.Index.GetNext();
        while (i < _data.ToExclusive)
        {
            _data.Action(i);
            i = _data.Index.GetNext();
        }

        _data.MarkThreadCompleted();
    }

    private class SharedCounter(int fromInclusive)
    {
        private int _index = fromInclusive;
        public int GetNext() => Interlocked.Increment(ref _index) - 1;
    }

    private class BaseData(int threads, int fromInclusive, int toExclusive)
    {
        public SharedCounter Index { get; } = new SharedCounter(fromInclusive);
        public int ToExclusive => toExclusive;
        public int ActiveThreads => Volatile.Read(ref threads);

        public int MarkThreadCompleted()
        {
            var remaining = Interlocked.Decrement(ref threads);

            if (remaining == 0)
            {
                lock (this)
                {
                    Monitor.Pulse(this);
                }
            }

            return remaining;
        }
    }

    private class Data(int threads, int fromInclusive, int toExclusive, Action<int> action) :
        BaseData(threads, fromInclusive, toExclusive)
    {
        public Action<int> Action => action;
    }

    private class InitProcessor<TLocal> : IThreadPoolWorkItem
    {
        private readonly Data<TLocal> _data;

        public static void For(
            int fromInclusive,
            int toExclusive,
            ParallelOptions parallelOptions,
            Func<TLocal>? init,
            TLocal? initValue,
            Func<int, TLocal, TLocal> action,
            Action<TLocal>? @finally = null)
        {
            var threads = parallelOptions.MaxDegreeOfParallelism > 0 ? parallelOptions.MaxDegreeOfParallelism : Environment.ProcessorCount;

            var data = new Data<TLocal>(threads, fromInclusive, toExclusive, action, init, initValue, @finally);

            for (int i = 0; i < threads - 1; i++)
            {
                ThreadPool.UnsafeQueueUserWorkItem(new InitProcessor<TLocal>(data), preferLocal: false);
            }

            new InitProcessor<TLocal>(data).Execute();

            if (data.ActiveThreads > 0)
            {
                lock (data)
                {
                    if (data.ActiveThreads > 0)
                    {
                        // Wait for remaining to complete
                        Monitor.Wait(data);
                    }
                }
            }
        }

        private InitProcessor(Data<TLocal> data) => _data = data;

        public void Execute()
        {
            TLocal? value = _data.Init();
            int i = _data.Index.GetNext();
            while (i < _data.ToExclusive)
            {
                value = _data.Action(i, value);
                i = _data.Index.GetNext();
            }

            _data.Finally(value);

            _data.MarkThreadCompleted();
        }

        private class Data<TValue>(int threads,
            int fromInclusive,
            int toExclusive,
            Func<int, TLocal, TLocal> action,
            Func<TValue>? init = null,
            TValue? initValue = default,
            Action<TValue>? @finally = null) : BaseData(threads, fromInclusive, toExclusive)
        {
            public Func<int, TLocal, TLocal> Action => action;

            public TValue Init() => initValue ?? (init is not null ? init.Invoke() : default)!;

            public void Finally(TValue value)
            {
                @finally?.Invoke(value);
            }
        }
    }
}
