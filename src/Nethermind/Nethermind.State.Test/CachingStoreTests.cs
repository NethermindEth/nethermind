// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
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
            _ = ctx.Wrapped.DidNotReceive()[Arg.Any<byte[]>()];
        }

        [Test]
        public void When_reading_values_stores_them_in_the_cache()
        {
            Context ctx = new(2);
            ctx.Wrapped[Arg.Any<byte[]>()].Returns(Value1);
            _ = ctx.Database[Key1];
            _ = ctx.Database[Key1];
            _ = ctx.Wrapped.Received(1)[Key1];
        }

        [Test]
        public void Uses_lru_strategy_when_caching_on_reads()
        {
            Context ctx = new(2);
            ctx.Wrapped[Arg.Any<byte[]>()].Returns(Value1);
            _ = ctx.Database[Key1];
            _ = ctx.Database[Key2];
            _ = ctx.Database[Key3];
            _ = ctx.Database[Key3];
            _ = ctx.Database[Key2];
            _ = ctx.Database[Key1];
            _ = ctx.Wrapped.Received(2)[Key1];
            _ = ctx.Wrapped.Received(1)[Key2];
            _ = ctx.Wrapped.Received(1)[Key3];
        }

        [Test]
        public void Uses_lru_strategy_when_caching_on_writes()
        {
            Context ctx = new(2);
            ctx.Wrapped[Arg.Any<byte[]>()].Returns(Value1);
            ctx.Database[Key1] = Value1;
            ctx.Database[Key2] = Value1;
            ctx.Database[Key3] = Value1;
            _ = ctx.Database[Key3];
            _ = ctx.Database[Key2];
            _ = ctx.Database[Key1];
            _ = ctx.Wrapped.Received(1)[Key1];
            _ = ctx.Wrapped.Received(0)[Key2];
            _ = ctx.Wrapped.Received(0)[Key3];
        }

        private class Context
        {
            public IKeyValueStoreWithBatching Wrapped { get; set; } = Substitute.For<IKeyValueStoreWithBatching>();

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
