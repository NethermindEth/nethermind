// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Nethermind.Core.Threading;

public static partial class Rayon
{
    /// <summary>
    /// The process-wide pool: one worker context per logical core, a global injector queue and the
    /// accounting that decides when an inactive context is activated on the thread pool.
    /// </summary>
    internal sealed class Registry
    {
        public static readonly Registry Instance = new(Cpu.RuntimeInformation.ProcessorCount);

        public readonly Worker[] Workers;
        private readonly ConcurrentQueue<Job> _injector = new();
        private CacheLinePaddedLong _inactiveCount;
        private CacheLinePaddedLong _searchingCount;
        private int _activateCursor;

        private Registry(int workerCount)
        {
            Workers = new Worker[workerCount];
            for (int i = 0; i < workerCount; i++)
            {
                Workers[i] = new Worker(this, i);
            }

            _inactiveCount = new CacheLinePaddedLong(workerCount);
        }

        public void Inject(Job job)
        {
            _injector.Enqueue(job);
            NotifyPushed();
        }

        public bool TryDequeueInjected([NotNullWhen(true)] out Job? job) => _injector.TryDequeue(out job);

        /// <summary>
        /// Called after every push or injection. Activates one inactive context only when nobody is
        /// already searching for work; a searching worker will pick the job up itself.
        /// </summary>
        public void NotifyPushed()
        {
            // Full fence pairs with the fence in Worker.Deactivate: either the pusher sees the context
            // become inactive or the deactivating worker sees the pushed job.
            Interlocked.MemoryBarrier();
            ActivateOneIfNoneSearching();
        }

        public void ActivateOneIfNoneSearching()
        {
            if (Volatile.Read(ref _inactiveCount.Value) > 0 && Volatile.Read(ref _searchingCount.Value) == 0)
            {
                Worker? worker = TryAcquire();
                if (worker is not null)
                {
                    ThreadPool.UnsafeQueueUserWorkItem(worker, preferLocal: false);
                }
            }
        }

        /// <summary>Claims an inactive context, for the caller to run on the current thread or to queue.</summary>
        public Worker? TryAcquire()
        {
            if (Volatile.Read(ref _inactiveCount.Value) == 0)
            {
                return null;
            }

            int start = Interlocked.Increment(ref _activateCursor);
            for (int k = 0; k < Workers.Length; k++)
            {
                Worker worker = Workers[(int)((uint)(start + k) % (uint)Workers.Length)];
                if (worker.TryActivate())
                {
                    return worker;
                }
            }

            return null;
        }

        public bool HasVisibleWork()
        {
            if (!_injector.IsEmpty)
            {
                return true;
            }

            for (int i = 0; i < Workers.Length; i++)
            {
                if (!Workers[i].Deque.IsEmpty)
                {
                    return true;
                }
            }

            return false;
        }

        public void StartSearching() => Interlocked.Increment(ref _searchingCount.Value);

        public void StopSearching() => Interlocked.Decrement(ref _searchingCount.Value);

        public void IncrementInactive() => Interlocked.Increment(ref _inactiveCount.Value);

        public void DecrementInactive() => Interlocked.Decrement(ref _inactiveCount.Value);
    }

    /// <summary>
    /// A worker context: a deque plus steal/sleep state. It runs on whichever thread pool thread picks it
    /// up (or on a caller's thread that acquired it) until it finds nothing to do, then goes inactive.
    /// </summary>
    internal sealed class Worker(Registry registry, int index) : IThreadPoolWorkItem
    {
        private const int Inactive = 0;
        private const int Active = 1;
        private const int Awake = 0;
        private const int Sleeping = 1;
        private const int Woken = 2;
        private const int RoundsUntilSleepy = 32;
        private const int RoundsUntilSleeping = 48;
        private const int MaxSpinShift = 6;

        [ThreadStatic]
        private static Worker? t_current;

        public static Worker? Current => t_current;

        public readonly WorkStealingDeque Deque = new();
        private readonly ManualResetEventSlim _event = new(false, spinCount: 0);
        private int _state;
        private int _sleepState;
        private uint _rng = (uint)(index + 1) * 2654435761u;

        public bool TryActivate()
        {
            if (Interlocked.CompareExchange(ref _state, Active, Inactive) != Inactive)
            {
                return false;
            }

            registry.DecrementInactive();
            return true;
        }

        /// <summary>Thread pool entry: run as a worker until there is nothing left to steal.</summary>
        public void Execute()
        {
            t_current = this;
            try
            {
                WaitUntil(null);
            }
            finally
            {
                t_current = null;
                Deactivate();
            }
        }

        /// <summary>Runs <paramref name="body"/> on the current thread as this (freshly acquired) context.</summary>
        public TResult RunAsCurrent<TState, TResult>(in TState state, Func<Worker, TState, TResult> body)
        {
            t_current = this;
            try
            {
                return body(this, state);
            }
            finally
            {
                t_current = null;
                Deactivate();
            }
        }

        private void Deactivate()
        {
            Deque.ClearIfEmpty();
            Volatile.Write(ref _state, Inactive);
            registry.IncrementInactive();
            // Full fence between going inactive and the re-check: pairs with Registry.NotifyPushed so a
            // job pushed meanwhile either activates us or is seen here.
            Interlocked.MemoryBarrier();
            if (registry.HasVisibleWork() && TryActivate())
            {
                ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
            }
        }

        public void Push(Job job)
        {
            Deque.Push(job);
            registry.NotifyPushed();
        }

        /// <summary>
        /// Runs other work until <paramref name="latch"/> is set: searches by spinning, then yielding,
        /// then blocks on the latch. With a null latch it is the worker's main loop, which returns
        /// (deactivating the context) once the search comes up empty.
        /// </summary>
        public void WaitUntil(Latch? latch)
        {
            int rounds = 0;
            registry.StartSearching();
            while (latch is null || !latch.IsSet)
            {
                if (FindWork(out Job? job))
                {
                    registry.StopSearching();
                    registry.ActivateOneIfNoneSearching();
                    job.Execute();
                    rounds = 0;
                    registry.StartSearching();
                }
                else if (rounds < RoundsUntilSleepy)
                {
                    Thread.SpinWait(1 << Math.Min(rounds, MaxSpinShift));
                    rounds++;
                }
                else if (rounds < RoundsUntilSleeping)
                {
                    Thread.Yield();
                    rounds++;
                }
                else if (latch is null)
                {
                    break;
                }
                else
                {
                    registry.StopSearching();
                    Sleep(latch);
                    registry.StartSearching();
                    rounds = 0;
                }
            }

            registry.StopSearching();
        }

        private bool FindWork([NotNullWhen(true)] out Job? job)
        {
            job = Deque.Pop();
            return job is not null || TrySteal(out job) || registry.TryDequeueInjected(out job);
        }

        private bool TrySteal([NotNullWhen(true)] out Job? job)
        {
            Worker[] workers = registry.Workers;
            int count = workers.Length;
            bool retry;
            do
            {
                retry = false;
                int start = (int)(NextRandom() % (uint)count);
                for (int k = 0; k < count; k++)
                {
                    int victim = start + k;
                    if (victim >= count)
                    {
                        victim -= count;
                    }

                    if (victim == index)
                    {
                        continue;
                    }

                    switch (workers[victim].Deque.TrySteal(out Job? stolen))
                    {
                        case WorkStealingDeque.StealResult.Success:
                            job = stolen!;
                            return true;
                        case WorkStealingDeque.StealResult.Abort:
                            // Lost a race on a non-empty deque: sweep again rather than give up.
                            retry = true;
                            break;
                    }
                }
            } while (retry);

            job = null;
            return false;
        }

        private uint NextRandom()
        {
            uint x = _rng;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            return _rng = x;
        }

        /// <summary>Blocks until the latch is set (its setter wakes us) or work becomes visible.</summary>
        private void Sleep(Latch latch)
        {
            // Reset before announcing so a Set() that follows the announcement cannot be lost.
            _event.Reset();
            Volatile.Write(ref _sleepState, Sleeping);
            // Full fence between the state store and the re-check below: pairs with Latch.Set.
            Interlocked.MemoryBarrier();
            if (latch.IsSet || registry.HasVisibleWork())
            {
                WakeSelf();
                return;
            }

            _event.Wait();
            WakeSelf();
        }

        private void WakeSelf()
        {
            if (Interlocked.CompareExchange(ref _sleepState, Awake, Sleeping) != Sleeping)
            {
                // A waker claimed us; clear its mark.
                Volatile.Write(ref _sleepState, Awake);
            }
        }

        public bool TryWake()
        {
            if (Interlocked.CompareExchange(ref _sleepState, Woken, Sleeping) != Sleeping)
            {
                return false;
            }

            _event.Set();
            return true;
        }
    }
}
