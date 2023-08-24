// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Witnesses;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.Store.Test.Witnesses
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class WitnessingStoreTests
    {
        [Test]
        public void Collects_on_reads()
        {
            Context context = new();
            context.Wrapped.ReadFunc = (key) => Value1;

            using IDisposable tracker = context.WitnessCollector.TrackOnThisThread();
            _ = context.Database[Key1];

            context.WitnessCollector.Collected.Should().HaveCount(1);
        }

        [Test]
        public void Does_not_collect_if_no_tracking()
        {
            Context context = new();
            context.Wrapped.ReadFunc = (key) => Value1;
            _ = context.Database[Key1];
            context.WitnessCollector.Collected.Should().HaveCount(0);
        }

        [Test]
        public void Collects_on_reads_when_cached_underneath()
        {
            Context context = new(2);
            context.Wrapped[Key1] = Value1;
            context.Wrapped[Key2] = Value2;
            context.Wrapped[Key3] = Value3;

            using IDisposable tracker = context.WitnessCollector.TrackOnThisThread();
            _ = context.Database[Key1];
            _ = context.Database[Key2];
            _ = context.Database[Key3];

            context.WitnessCollector.Collected.Should().HaveCount(3);

            context.WitnessCollector.Reset();
            _ = context.Database[Key1];
            _ = context.Database[Key2];
            _ = context.Database[Key3];

            context.WitnessCollector.Collected.Should().HaveCount(3);
        }

        [Test]
        public void Collects_on_reads_when_cached_underneath_and_previously_populated()
        {
            Context context = new(3);

            using IDisposable tracker = context.WitnessCollector.TrackOnThisThread();
            context.Database[Key1] = Value1;
            context.Database[Key2] = Value1;
            context.Database[Key3] = Value1;
            context.WitnessCollector.Collected.Should().HaveCount(0);
            _ = context.Database[Key1];
            _ = context.Database[Key2];
            _ = context.Database[Key3];

            context.WitnessCollector.Collected.Should().HaveCount(3);
        }

        [Test]
        public void Does_not_collect_on_writes()
        {
            Context context = new();
            context.Database[Key1] = Value1;
            context.WitnessCollector.Collected.Should().HaveCount(0);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(31)]
        [TestCase(33)]
        public void Only_works_with_32_bytes_keys(int keyLength)
        {
            Context context = new();
            context.Wrapped.ReadFunc = (key) => Bytes.Empty;

            Assert.Throws<NotSupportedException>(
                () => _ = context.Database[new byte[keyLength]]);
        }

        private class Context
        {
            public TestMemDb Wrapped { get; } = new TestMemDb();

            public WitnessingStore Database { get; }

            public IWitnessCollector WitnessCollector { get; }

            public Context()
            {
                WitnessCollector = new WitnessCollector(new MemDb(), LimboLogs.Instance);
                Database = new WitnessingStore(Wrapped, WitnessCollector);
            }

            public Context(int cacheSize)
            {
                WitnessCollector = new WitnessCollector(new MemDb(), LimboLogs.Instance);
                Database = new WitnessingStore(new CachingStore(Wrapped, cacheSize), WitnessCollector);
            }
        }

        private static readonly byte[] Key1 = TestItem.KeccakA.BytesToArray();

        private static readonly byte[] Key2 = TestItem.KeccakB.BytesToArray();

        private static readonly byte[] Key3 = TestItem.KeccakC.BytesToArray();

        private static readonly byte[] Value1 = { 1 };

        private static readonly byte[] Value2 = { 2 };

        private static readonly byte[] Value3 = { 3 };
    }
}
