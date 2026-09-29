// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core.Caching;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test.Caching
{
    [TestFixture]
    public class LruCacheTests
    {
        private static ICache<Address, Account> Create() => new LruCache<Address, Account>(Capacity, Capacity / 2, "test");

        private const int Capacity = 32;

        private readonly Account[] _accounts = new Account[Capacity * 2 + 1];
        private readonly Address[] _addresses = new Address[Capacity * 2 + 1];

        [SetUp]
        public void Setup()
        {
            for (int i = 0; i < Capacity * 2; i++)
            {
                _accounts[i] = Build.An.Account.WithBalance((UInt256)i).TestObject;
                _addresses[i] = Build.An.Address.FromNumber(i).TestObject;
            }
        }

        [Test]
        public void At_capacity()
        {
            ICache<Address, Account> cache = Create();
            for (int i = 0; i < Capacity; i++)
            {
                Assert.That(cache.Set(_addresses[i], _accounts[i]), Is.True);
            }

            Account? account = cache.Get(_addresses[Capacity - 1]);
            Assert.That(account, Is.EqualTo(_accounts[Capacity - 1]));
        }

        [Test]
        public void Can_reset()
        {
            ICache<Address, Account> cache = Create();
            Assert.That(cache.Set(_addresses[0], _accounts[0]), Is.True);
            Assert.That(cache.Set(_addresses[0], _accounts[1]), Is.False);
            Assert.That(cache.Get(_addresses[0]), Is.EqualTo(_accounts[1]));
        }

        [Test]
        public void Can_ask_before_first_set()
        {
            ICache<Address, Account> cache = Create();
            Assert.That(cache.Get(_addresses[0]), Is.Null);
        }

        [Test]
        public void Can_clear()
        {
            ICache<Address, Account> cache = Create();
            Assert.That(cache.Set(_addresses[0], _accounts[0]), Is.True);
            cache.Clear();
            Assert.That(cache.Get(_addresses[0]), Is.Null);
            Assert.That(cache.Set(_addresses[0], _accounts[1]), Is.True);
            Assert.That(cache.Get(_addresses[0]), Is.EqualTo(_accounts[1]));
        }

        [Test]
        public void Beyond_capacity_lru()
        {
            ICache<Address, Account> cache = Create();
            for (int i = 0; i < Capacity * 2; i++)
            {
                for (int ii = 0; ii < Capacity / 2; ii++)
                {
                    cache.Set(_addresses[i], _accounts[i]);
                }
                cache.Set(_addresses[i], _accounts[i]);
            }
        }

        [Test]
        public void Beyond_capacity_lru_check()
        {
            Random random = new();
            ICache<Address, Account> cache = Create();
            for (int iter = 0; iter < Capacity; iter++)
            {
                for (int ii = 0; ii < Capacity; ii++)
                {
                    Assert.That(cache.Set(_addresses[ii], _accounts[ii]), Is.True);
                }

                for (int i = 1; i < Capacity; i++)
                {
                    for (int ii = i - 1; ii < i - 1 + Capacity; ii++)
                    {
                        // Fuzz the order of the addresses
                        int index = random.Next(i - 1, i - 1 + Capacity);
                        Assert.That(cache.Set(_addresses[index], _accounts[index]), Is.False);
                    }
                    for (int ii = i - 1; ii < i - 1 + Capacity; ii++)
                    {
                        // Fuzz the order of the addresses
                        int index = random.Next(i - 1, i - 1 + Capacity);
                        Assert.That(cache.Delete(_addresses[index]), Is.True);
                        Assert.That(cache.Set(_addresses[index], _accounts[index]), Is.True);
                    }
                    for (int ii = i - 1; ii < i - 1 + Capacity; ii++)
                    {
                        // Fuzz the order of the addresses
                        int index = random.Next(i - 1, i - 1 + Capacity);
                        Assert.That(cache.Get(_addresses[index]), Is.EqualTo(_accounts[index]));
                    }
                    for (int ii = i; ii < i + Capacity; ii++)
                    {
                        if (ii < i + Capacity - 1)
                            Assert.That(cache.Set(_addresses[ii], _accounts[ii]), Is.False);
                        else
                            Assert.That(cache.Set(_addresses[ii], _accounts[ii]), Is.True);
                    }
                    for (int ii = i; ii < i + Capacity; ii++)
                    {
                        Assert.That(cache.Get(_addresses[ii]), Is.Not.Null);
                    }
                    if (i > 0)
                    {
                        Assert.That(cache.Get(_addresses[i - 1]), Is.Null);
                    }
                    Assert.That(cache.Get(_addresses[i + Capacity]), Is.Null);
                }

                Assert.That(cache.Count, Is.EqualTo(Capacity));
                if (iter % 2 == 0)
                {
                    cache.Clear();
                }
                else
                {
                    for (int ii = Capacity - 1; ii < Capacity * 2 - 1; ii++)
                    {
                        Assert.That(cache.Get(_addresses[ii]), Is.EqualTo(_accounts[ii]));
                        Assert.That(cache.Delete(_addresses[ii]), Is.True);
                    }
                }

                Assert.That(cache.Count, Is.EqualTo(0));
            }
        }

        [Test]
        public void Beyond_capacity_lru_parallel()
        {
            ICache<Address, Account> cache = Create();
            Parallel.For(0, Math.Min(Environment.ProcessorCount * 8, 64), (iter) =>
            {
                for (int ii = 0; ii < Capacity; ii++)
                {
                    cache.Set(_addresses[ii], _accounts[ii]);
                }

                for (int i = 1; i < Capacity; i++)
                {
                    for (int ii = i; ii < i + Capacity; ii++)
                    {
                        cache.Set(_addresses[ii], _accounts[ii]);
                    }
                    for (int ii = i; ii < i + Capacity; ii++)
                    {
                        cache.Get(_addresses[ii]);
                    }
                    if (i > 0)
                    {
                        cache.Get(_addresses[i - 1]);
                    }
                    cache.Get(_addresses[i + Capacity]);

                    if (iter % Environment.ProcessorCount == 0)
                    {
                        cache.Clear();
                    }
                    else
                    {
                        for (int ii = i; ii < i + Capacity / 2; ii++)
                        {
                            cache.Delete(_addresses[ii]);
                        }
                    }
                }
            });
        }

        [Test]
        public void Beyond_capacity()
        {
            ICache<Address, Account> cache = Create();
            for (int i = 0; i < Capacity * 2; i++)
            {
                Assert.That(cache.Set(_addresses[i], _accounts[i]), Is.True);
            }

            for (int i = 0; i < Capacity; i++)
            {
                Assert.That(cache.Get(_addresses[i]), Is.Null);
            }
            // Check in reverse order
            for (int i = Capacity * 2 - 1; i >= Capacity; i--)
            {
                Assert.That(cache.Get(_addresses[i]), Is.EqualTo(_accounts[i]));
            }
        }

        [Test]
        public void Can_set_and_then_set_null()
        {
            ICache<Address, Account> cache = Create();
            Assert.That(cache.Set(_addresses[0], _accounts[0]), Is.True);
            Assert.That(cache.Set(_addresses[0], _accounts[0]), Is.False);
            Assert.That(cache.Set(_addresses[0], null!), Is.True);
            Assert.That(cache.Get(_addresses[0]), Is.EqualTo(null));
        }

        [Test]
        public void Can_delete()
        {
            ICache<Address, Account> cache = Create();
            cache.Set(_addresses[0], _accounts[0]);
            Assert.That(cache.Delete(_addresses[0]), Is.True);
            Assert.That(cache.Get(_addresses[0]), Is.EqualTo(null));
            Assert.That(cache.Delete(_addresses[0]), Is.False);
        }

        [Test]
        public void Can_remove_and_return_value()
        {
            LruCache<Address, Account> cache = new(Capacity, "test");
            cache.Set(_addresses[0], _accounts[0]);

            Assert.That(cache.TryRemove(_addresses[0], out Account? removed), Is.True);
            Assert.That(removed, Is.EqualTo(_accounts[0]));
            Assert.That(cache.TryRemove(_addresses[0], out removed), Is.False);
            Assert.That(removed, Is.Null);
        }

        [Test]
        public void Evict_is_called_when_capacity_replaces_oldest()
        {
            int evicted = 0;
            LruCache<int, int> cache = new TestEvictingLruCache<int, int>(2, "test", value => evicted = value);

            cache.Set(1, 10);
            cache.Set(2, 20);
            cache.Set(3, 30);

            Assert.That(evicted, Is.EqualTo(10));
        }

        [Test]
        public void Evict_is_called_when_existing_value_is_replaced()
        {
            int evicted = 0;
            LruCache<int, int> cache = new TestEvictingLruCache<int, int>(2, "test", value => evicted = value);

            cache.Set(1, 10);
            cache.Set(1, 11);

            Assert.That(evicted, Is.EqualTo(10));
            Assert.That(cache.Get(1), Is.EqualTo(11));
        }

        [Test]
        public void TryRemove_returns_value_without_calling_evict()
        {
            int evicted = 0;
            LruCache<int, int> cache = new TestEvictingLruCache<int, int>(2, "test", value => evicted = value);
            cache.Set(1, 10);

            Assert.That(cache.TryRemove(1, out int removed), Is.True);

            Assert.That(removed, Is.EqualTo(10));
            Assert.That(evicted, Is.Zero);
        }

        [Test]
        public void Disposing_cache_disposes_evicted_values()
        {
            DisposingLruCache<int, DisposableValue> cache = new(1, "test");
            DisposableValue evicted = new();

            cache.Set(1, evicted);
            cache.Set(2, new DisposableValue());

            Assert.That(evicted.IsDisposed, Is.True);
        }

        [Test]
        public void Disposing_cache_try_remove_transfers_ownership()
        {
            DisposingLruCache<int, DisposableValue> cache = new(1, "test");
            DisposableValue removed = new();
            cache.Set(1, removed);

            Assert.That(cache.TryRemove(1, out DisposableValue? actual), Is.True);

            Assert.That(actual, Is.SameAs(removed));
            Assert.That(removed.IsDisposed, Is.False);
        }

        [Test]
        public void Clear_should_free_all_capacity()
        {
            ICache<Address, Account> cache = Create();
            for (int i = 0; i < Capacity; i++)
            {
                cache.Set(_addresses[i], _accounts[i]);
            }

            cache.Clear();

            static int MapForRefill(int index) => (index + 1) % Capacity;

            // fill again
            for (int i = 0; i < Capacity; i++)
            {
                cache.Set(_addresses[i], _accounts[MapForRefill(i)]);
            }

            // validate
            for (int i = 0; i < Capacity; i++)
            {
                Assert.That(cache.Get(_addresses[i]), Is.EqualTo(_accounts[MapForRefill(i)]));
            }
        }

        [TestCase(EvictionOperation.Delete, false)]
        [TestCase(EvictionOperation.ReplaceExisting, true)]
        [TestCase(EvictionOperation.ReplaceOldest, false)]
        [TestCase(EvictionOperation.Clear, false)]
        public async Task Evict_is_invoked_outside_lock(EvictionOperation operation, bool expectedContainsResult)
        {
            LruCache<int, int> cache = null!;
            TaskCompletionSource<bool> evictResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
            cache = new TestEvictingLruCache<int, int>(2, "test", _ => evictResult.SetResult(cache.Contains(1)));
            cache.Set(1, 10);
            if (operation == EvictionOperation.ReplaceOldest)
            {
                cache.Set(2, 20);
            }

            Task operationTask = Task.Run(() => RunEvictionOperation(cache, operation));
            Task completedTask = await Task.WhenAny(operationTask, Task.Delay(TimeSpan.FromSeconds(5)));

            Assert.That(completedTask, Is.SameAs(operationTask));
            await operationTask;
            Assert.That(await evictResult.Task.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(expectedContainsResult));
        }

        [Test]
        public void Delete_keeps_internal_structure()
        {
            int maxCapacity = 32;
            int itemsToKeep = 10;
            int iterations = 40;

            LruCache<int, int> cache = new(maxCapacity, "test");

            for (int i = 0; i < iterations; i++)
            {
                cache.Set(i, i);
                cache.Delete(i - itemsToKeep);
            }

            int count = 0;

            for (int i = 0; i < iterations; i++)
            {
                if (cache.TryGet(i, out int val))
                {
                    count++;
                    Assert.That(val, Is.EqualTo(i));
                }
            }

            Assert.That(count, Is.EqualTo(itemsToKeep));
        }

        [Test]
        public void Wrong_capacity_number_at_constructor()
        {
            int maxCapacity = 0;

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                {
                    _ = new LruCache<int, int>(maxCapacity, "test");
                });

        }

        private static void RunEvictionOperation(LruCache<int, int> cache, EvictionOperation operation)
        {
            switch (operation)
            {
                case EvictionOperation.Delete:
                    cache.Delete(1);
                    return;
                case EvictionOperation.ReplaceExisting:
                    cache.Set(1, 11);
                    return;
                case EvictionOperation.ReplaceOldest:
                    cache.Set(3, 30);
                    return;
                case EvictionOperation.Clear:
                    cache.Clear();
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
            }
        }

        public enum EvictionOperation
        {
            Delete,
            ReplaceExisting,
            ReplaceOldest,
            Clear
        }

        private sealed class TestEvictingLruCache<TKey, TValue>(
            int maxCapacity,
            string name,
            Action<TValue> evict) : LruCache<TKey, TValue>(maxCapacity, name)
            where TKey : notnull
        {
            protected override void Evict(TValue value) => evict(value);
        }

        private sealed class DisposableValue : IDisposable
        {
            public bool IsDisposed { get; private set; }

            public void Dispose() => IsDisposed = true;
        }
    }
}
