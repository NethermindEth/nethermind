// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Store.Test
{
    [Parallelizable(ParallelScope.All)]
    public class CachingStoreTests
    {
        [Test]
        public void When_setting_values_stores_them_in_the_cache()
        {
            Context ctx = new(2);
            ctx.Database[Key1] = Value1;
            _ = ctx.Database[Key1];
            ctx.Wrapped.KeyWasWritten(Key3, 0);
        }

        [Test]
        public void When_reading_values_stores_them_in_the_cache()
        {
            Context ctx = new(2);
            ctx.Wrapped.ReadFunc = (key) => Value1;
            _ = ctx.Database[Key1];
            _ = ctx.Database[Key1];
            ctx.Wrapped.KeyWasRead(Key1);
        }

        [Test]
        public void When_reading_values_with_flags_forward_the_flags()
        {
            Context ctx = new(2);
            ctx.Wrapped.ReadFunc = (key) => Value1;
            _ = ctx.Database.Get(Key1, ReadFlags.HintReadAhead);
            ctx.Wrapped.KeyWasReadWithFlags(Key1, ReadFlags.HintReadAhead);
        }

        [Test]
        public void Uses_lru_strategy_when_caching_on_reads()
        {
            Context ctx = new(2);
            ctx.Wrapped.ReadFunc = (key) => Value1;
            _ = ctx.Database[Key1];
            _ = ctx.Database[Key2];
            _ = ctx.Database[Key3];
            _ = ctx.Database[Key3];
            _ = ctx.Database[Key2];
            _ = ctx.Database[Key1];
            ctx.Wrapped.KeyWasRead(Key1, 2);
            ctx.Wrapped.KeyWasRead(Key2, 1);
            ctx.Wrapped.KeyWasRead(Key3, 1);
        }

        [Test]
        public void Uses_lru_strategy_when_caching_on_writes()
        {
            Context ctx = new(2);
            ctx.Wrapped.ReadFunc = (key) => Value1;
            ctx.Database[Key1] = Value1;
            ctx.Database[Key2] = Value1;
            ctx.Database[Key3] = Value1;
            _ = ctx.Database[Key3];
            _ = ctx.Database[Key2];
            _ = ctx.Database[Key1];
            ctx.Wrapped.KeyWasRead(Key1, 1);
            ctx.Wrapped.KeyWasRead(Key2, 0);
            ctx.Wrapped.KeyWasRead(Key3, 0);
        }

        [Test]
        public void LruCache_AddingItems()
        {
            var cache = new LruCache<int, string>(maxCapacity: 2, startCapacity: 2, "test");

            cache.Set(1, "one");
            cache.Set(2, "two");

            Assert.IsTrue(cache.TryGet(1, out _));
            Assert.IsTrue(cache.TryGet(2, out _));
        }

        [Test]
        public void LruCache_Eviction()
        {
            var cache = new LruCache<int, string>(maxCapacity: 2, startCapacity: 2, "test");

            cache.Set(1, "one");
            cache.Set(2, "two");
            cache.Set(3, "three");  // This should evict "one"

            Assert.IsFalse(cache.TryGet(1, out _));
            Assert.IsTrue(cache.TryGet(2, out _));
            Assert.IsTrue(cache.TryGet(3, out _));
        }

        [Test]
        public void LruCache_Reordering()
        {
            var cache = new LruCache<int, string>(maxCapacity: 3, startCapacity: 3, "test");

            cache.Set(1, "one");
            cache.Set(2, "two");
            cache.Set(3, "three"); // At this point, "one" should be the LRU item

            cache.TryGet(1, out _); // This should move "one" to the MRU position

            cache.Set(4, "four"); // This should evict "two", which is now LRU

            Assert.IsFalse(cache.TryGet(2, out _));
            Assert.IsTrue(cache.TryGet(1, out _));
            Assert.IsTrue(cache.TryGet(3, out _));
            Assert.IsTrue(cache.TryGet(4, out _));
        }

        [Test]
        public void LruCache_SingleUseMultiUseRatio()
        {
            LruCache<int, string> cache = new (maxCapacity: 4, startCapacity: 4, "test");

            cache.Set(1, "one");
            cache.Set(2, "two");
            cache.Set(3, "three");
            cache.Set(4, "four");

            // At this point, all items are single-use
            Assert.That(cache.SingleAccessCount, Is.EqualTo(4));
            Assert.That(cache.MultiAccessCount, Is.EqualTo(0));

            // Access an item to promote it to multi-use
            cache.TryGet(1, out _);
            Assert.That(cache.SingleAccessCount, Is.EqualTo(3));
            Assert.That(cache.MultiAccessCount, Is.EqualTo(1));

            // Add a new item, this should evict a single-use item
            cache.Set(5, "five");
            Assert.That(cache.SingleAccessCount, Is.EqualTo(3));
            Assert.That(cache.MultiAccessCount, Is.EqualTo(1));
        }

        [Test]
        public void LruCache_Remove()
        {
            LruCache<int, string> cache = new (maxCapacity: 4, startCapacity: 4, "test");

            cache.Set(1, "one");
            cache.Set(2, "two");
            cache.Set(3, "three");
            cache.Set(4, "four");

            Assert.IsTrue(cache.Delete(1));
            Assert.IsFalse(cache.TryGet(1, out _));
            Assert.That(cache.SingleAccessCount, Is.EqualTo(3));
            Assert.That(cache.MultiAccessCount, Is.EqualTo(0));

            cache.TryGet(2, out _);
            Assert.IsTrue(cache.Delete(2));
            Assert.IsFalse(cache.TryGet(2, out _));
            Assert.That(cache.SingleAccessCount, Is.EqualTo(2));
            Assert.That(cache.MultiAccessCount, Is.EqualTo(0));
        }

        [Test]
        public void LruCache_DeleteFromMiddleOfSections()
        {
            LruCache<int, string> cache = new (maxCapacity: 6, startCapacity: 6, "test");

            cache.Set(1, "one");
            cache.Set(2, "two");
            cache.Set(3, "three");
            cache.Set(4, "four");

            // Accessing an item to promote it to multi-use
            cache.TryGet(2, out _);

            cache.Set(5, "five");
            cache.Set(6, "six");

            // At this point, cache order should be 2 (multi-use), 1, 3, 4, 5, 6 (single-use)

            // Delete an item from the middle of the single-use section (4)
            Assert.That(cache.SingleAccessCount, Is.EqualTo(5));
            Assert.IsTrue(cache.Delete(4));
            Assert.IsFalse(cache.TryGet(4, out _));
            Assert.That(cache.SingleAccessCount, Is.EqualTo(4));
            Assert.That(cache.MultiAccessCount, Is.EqualTo(1));

            // Delete an item from the multi-use section (2)
            Assert.IsTrue(cache.Delete(2));
            Assert.IsFalse(cache.TryGet(2, out _));
            Assert.That(cache.SingleAccessCount, Is.EqualTo(4));
            Assert.That(cache.MultiAccessCount, Is.EqualTo(0));
        }

        private class Context
        {
            public TestMemDb Wrapped { get; set; } = new();

            public CachingStore Database { get; set; }

            public Context(int size)
            {
                Database = new CachingStore(Wrapped, size);
            }
        }

        private static readonly byte[] Key1 = { 1 };

        private static readonly byte[] Key2 = { 2 };

        private static readonly byte[] Key3 = { 3 };

        private static readonly byte[] Value1 = { 1 };

        private static readonly byte[] Value2 = { 2 };

        private static readonly byte[] Value3 = { 3 };
    }
}
