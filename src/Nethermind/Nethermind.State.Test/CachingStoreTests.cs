// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
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
