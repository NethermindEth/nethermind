// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Nethermind.Core.Threading;

public static partial class Rayon
{
    /// <summary>The process-wide pool: one worker per logical core, a global injector queue and sleep accounting.</summary>
    internal sealed class Registry
    {
        private const int WorkerStackSize = 16 * 1024 * 1024;

        public static readonly Registry Instance = new(Cpu.RuntimeInformation.ProcessorCount);

        public readonly Worker[] Workers;
        private readonly ConcurrentQueue<Job> _injector = new();
        private CacheLinePaddedLong _sleepingCount;
        private CacheLinePaddedLong _searchingCount;
        private int _wakeCursor;

        private Registry(int workerCount)
        {
            Workers = new Worker[workerCount];
            for (int i = 0; i < workerCount; i++)
            {
                Workers[i] = new Worker(this, i);
            }

            // Start only once every worker exists: the steal sweep visits all of them.
            for (int i = 0; i < workerCount; i++)
            {
                Thread thread = new(Workers[i].RunLoop, WorkerStackSize)
                {
                    IsBackground = true,
                    Name = $"Rayon worker {i}",
                };
                thread.Start();
            }
        }

        public void Inject(Job job)
        {
            _injector.Enqueue(job);
            NotifyPushed();
        }

        public bool TryDequeueInjected([NotNullWhen(true)] out Job? job) => _injector.TryDequeue(out job);

        /// <summary>
        /// Called after every push or injection. Wakes one sleeper only when nobody is already searching
        /// for work; a searching worker will pick the job up itself.
        /// </summary>
        public void NotifyPushed()
        {
            // Full fence pairs with the fence in Worker.Sleep: either the pusher sees the sleeper's
            // announcement or the sleeper sees the pushed job.
            Interlocked.MemoryBarrier();
            WakeOneIfNoneSearching();
        }

        public void WakeOneIfNoneSearching()
        {
            if (Volatile.Read(ref _sleepingCount.Value) > 0 && Volatile.Read(ref _searchingCount.Value) == 0)
            {
                WakeOne();
            }
        }

        public void StartSearching() => Interlocked.Increment(ref _searchingCount.Value);

        public void StopSearching() => Interlocked.Decrement(ref _searchingCount.Value);

        private void WakeOne()
        {
            int start = Interlocked.Increment(ref _wakeCursor);
            for (int k = 0; k < Workers.Length; k++)
            {
                if (Workers[(int)((uint)(start + k) % (uint)Workers.Length)].TryWake())
                {
                    return;
                }
            }
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

        public void IncrementSleeping() => Interlocked.Increment(ref _sleepingCount.Value);

        public void DecrementSleeping() => Interlocked.Decrement(ref _sleepingCount.Value);
    }

    /// <summary>A pool thread: owns a deque, steals from the others, and parks when there is nothing to do.</summary>
    internal sealed class Worker(Registry registry, int index)
    {
        private const int Awake = 0;
        private const int Sleeping = 1;
        private const int Woken = 2;
        // Spin briefly, then yield for about a millisecond before parking: fork-join bursts (a state
        // tree flush, then its storage tries) arrive close together, and a parked worker costs a
        // futex wake plus a C-state exit (~50-150us) per burst otherwise.
        private const int RoundsUntilSleepy = 32;
        private const int RoundsUntilSleeping = RoundsUntilSleepy + 256;
        private const int MaxSpinShift = 6;

        [ThreadStatic]
        private static Worker? t_current;

        public static Worker? Current => t_current;

        public readonly WorkStealingDeque Deque = new();
        private readonly ManualResetEventSlim _event = new(false, spinCount: 0);
        private int _sleepState;
        private uint _rng = (uint)(index + 1) * 2654435761u;

        public void RunLoop()
        {
            t_current = this;
            WaitUntil(null);
        }

        public void Push(Job job)
        {
            Deque.Push(job);
            registry.NotifyPushed();
        }

        /// <summary>
        /// Runs other work until <paramref name="latch"/> is set (forever when null): searches by
        /// spinning, then yielding, then parks when there is nothing to steal. A searcher that finds
        /// work passes the searching role to a sleeper, so ramp-up follows the available work.
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
                    registry.WakeOneIfNoneSearching();
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

        private void Sleep(Latch? latch)
        {
            // Reset before announcing so a Set() that follows the announcement cannot be lost.
            _event.Reset();
            registry.IncrementSleeping();
            Volatile.Write(ref _sleepState, Sleeping);
            // Full fence between the state store and the re-check below: pairs with
            // Registry.NotifyPushed so a push is either seen here or wakes us.
            Interlocked.MemoryBarrier();
            if ((latch is not null && latch.IsSet) || registry.HasVisibleWork())
            {
                WakeSelf();
                return;
            }

            _event.Wait();
            WakeSelf();
        }

        private void WakeSelf()
        {
            if (Interlocked.CompareExchange(ref _sleepState, Awake, Sleeping) == Sleeping)
            {
                registry.DecrementSleeping();
            }
            else
            {
                // A waker claimed us and already adjusted the sleeping count.
                Volatile.Write(ref _sleepState, Awake);
            }
        }

        public bool TryWake()
        {
            if (Interlocked.CompareExchange(ref _sleepState, Woken, Sleeping) != Sleeping)
            {
                return false;
            }

            registry.DecrementSleeping();
            _event.Set();
            return true;
        }
    }
}
