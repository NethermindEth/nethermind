// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using Nethermind.Db.Rocks;
using NUnit.Framework;

namespace Nethermind.Db.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class DisposableLazyTests
    {
        private const int RaceRepeatCount = 20;

        [Test]
        public void Value_is_not_created_until_read()
        {
            int factoryCalls = 0;
            DisposableLazy<TrackedDisposable> lazy = new(() =>
            {
                factoryCalls++;
                return new TrackedDisposable();
            });

            Assert.That(factoryCalls, Is.Zero);

            _ = lazy.Value;

            Assert.That(factoryCalls, Is.EqualTo(1));
        }

        [Test]
        public void Value_creates_once_under_concurrent_reads()
        {
            int factoryCalls = 0;
            TrackedDisposable tracked = new();
            DisposableLazy<TrackedDisposable> lazy = new(() =>
            {
                Interlocked.Increment(ref factoryCalls);
                return tracked;
            });

            TrackedDisposable[] results = new TrackedDisposable[3];
            Action[] readers = new Action[results.Length];
            for (int i = 0; i < results.Length; i++)
            {
                int index = i;
                readers[index] = () => results[index] = lazy.Value;
            }

            RunConcurrently(readers);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(factoryCalls, Is.EqualTo(1));
                Assert.That(results, Is.All.SameAs(tracked));
            }
        }

        [Test]
        public void Dispose_disposes_created_value()
        {
            TrackedDisposable tracked = new();
            DisposableLazy<TrackedDisposable> lazy = new(() => tracked);

            _ = lazy.Value;
            lazy.Dispose();

            Assert.That(tracked.DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public void Dispose_does_not_create_value()
        {
            int factoryCalls = 0;
            DisposableLazy<TrackedDisposable> lazy = new(() =>
            {
                factoryCalls++;
                return new TrackedDisposable();
            });

            lazy.Dispose();

            Assert.That(factoryCalls, Is.Zero);
        }

        [Test]
        public void Dispose_is_idempotent()
        {
            TrackedDisposable tracked = new();
            DisposableLazy<TrackedDisposable> lazy = new(() => tracked);

            _ = lazy.Value;
            lazy.Dispose();
            lazy.Dispose();

            Assert.That(tracked.DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public void Value_after_Dispose_throws_when_not_created()
        {
            DisposableLazy<TrackedDisposable> lazy = new(static () => new TrackedDisposable());

            lazy.Dispose();

            Assert.That(() => lazy.Value, Throws.InstanceOf<ObjectDisposedException>());
        }

        [Test]
        public void Value_after_Dispose_returns_value_when_already_created()
        {
            TrackedDisposable tracked = new();
            DisposableLazy<TrackedDisposable> lazy = new(() => tracked);

            _ = lazy.Value;
            lazy.Dispose();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(lazy.Value, Is.SameAs(tracked));
                Assert.That(tracked.DisposeCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void Factory_exception_propagates_and_Dispose_is_safe()
        {
            DisposableLazy<TrackedDisposable> lazy = new(static () => throw new InvalidOperationException("factory failed"));

            Assert.That(() => lazy.Value, Throws.InstanceOf<InvalidOperationException>());
            Assert.That(() => lazy.Value, Throws.InstanceOf<InvalidOperationException>());
            Assert.That(lazy.Dispose, Throws.Nothing);
        }

        [Test]
        [Repeat(RaceRepeatCount)]
        public void Value_race_with_Dispose_never_leaks()
        {
            int factoryCalls = 0;
            TrackedDisposable tracked = new();
            DisposableLazy<TrackedDisposable> lazy = new(() =>
            {
                Interlocked.Increment(ref factoryCalls);
                return tracked;
            });

            void TryRead()
            {
                try { _ = lazy.Value; }
                catch (ObjectDisposedException) { }
            }

            RunConcurrently(TryRead, TryRead, TryRead, lazy.Dispose);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(factoryCalls, Is.LessThanOrEqualTo(1));
                Assert.That(tracked.DisposeCount, Is.EqualTo(factoryCalls), "a created value was left undisposed");
            }
        }

        /// <summary>
        /// Runs each action on its own thread, released together by a barrier.
        /// Dedicated threads instead of the pool, so a busy pool cannot stall the barrier.
        /// </summary>
        private static void RunConcurrently(params Action[] actions)
        {
            using Barrier barrier = new(actions.Length);
            Exception? failure = null;

            Thread[] threads = new Thread[actions.Length];
            for (int i = 0; i < actions.Length; i++)
            {
                Action action = actions[i];
                threads[i] = new Thread(() =>
                {
                    // ReSharper disable once AccessToDisposedClosure
                    barrier.SignalAndWait();

                    try { action(); }
                    catch (Exception e) { Interlocked.CompareExchange(ref failure, e, null); }
                })
                { IsBackground = true };
            }

            foreach (Thread thread in threads) thread.Start();
            foreach (Thread thread in threads) thread.Join();

            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private sealed class TrackedDisposable : IDisposable
        {
            private int _disposeCount;
            public int DisposeCount => Volatile.Read(ref _disposeCount);
            public void Dispose() => Interlocked.Increment(ref _disposeCount);
        }
    }
}
