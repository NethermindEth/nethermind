// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Witnesses;
using NUnit.Framework;

namespace Nethermind.Store.Test.Witnesses
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class WitnessCollectorTests
    {
        [Test]
        public void Collects_each_cache_once()
        {
            WitnessCollector witnessCollector = new(new MemDb(), LimboLogs.Instance);

            using IDisposable tracker = witnessCollector.TrackOnThisThread();
            witnessCollector.Add(Keccak.Zero);
            witnessCollector.Add(Keccak.Zero);

            witnessCollector.Collected.Should().HaveCount(1);
        }

        [Test]
        public void Can_collect_many()
        {
            WitnessCollector witnessCollector = new(new MemDb(), LimboLogs.Instance);

            using IDisposable tracker = witnessCollector.TrackOnThisThread();
            witnessCollector.Add(TestItem.KeccakA);
            witnessCollector.Add(TestItem.KeccakB);

            witnessCollector.Collected.Should().HaveCount(2);
        }

        [Test]
        public void Can_reset()
        {
            WitnessCollector witnessCollector = new(new MemDb(), LimboLogs.Instance);

            using IDisposable tracker = witnessCollector.TrackOnThisThread();
            witnessCollector.Add(TestItem.KeccakA);
            witnessCollector.Add(TestItem.KeccakB);
            witnessCollector.Reset();

            witnessCollector.Collected.Should().HaveCount(0);
        }

        [Test]
        public void Can_collect_after_reset()
        {
            WitnessCollector witnessCollector = new(new MemDb(), LimboLogs.Instance);

            using IDisposable tracker = witnessCollector.TrackOnThisThread();
            witnessCollector.Add(TestItem.KeccakA);
            witnessCollector.Add(TestItem.KeccakB);
            witnessCollector.Reset();
            witnessCollector.Add(TestItem.KeccakC);

            witnessCollector.Collected.Should().HaveCount(1);
        }

        [Test]
        public void Collects_what_it_should_collect()
        {
            WitnessCollector witnessCollector = new(new MemDb(), LimboLogs.Instance);

            using IDisposable tracker = witnessCollector.TrackOnThisThread();
            witnessCollector.Add(TestItem.KeccakA);
            witnessCollector.Add(TestItem.KeccakB);

            witnessCollector.Collected.Should().Contain(TestItem.KeccakA);
            witnessCollector.Collected.Should().Contain(TestItem.KeccakB);
        }

        [Test]
        public void Can_reset_empty()
        {
            WitnessCollector witnessCollector = new(new MemDb(), LimboLogs.Instance);

            using IDisposable tracker = witnessCollector.TrackOnThisThread();
            witnessCollector.Reset();

            witnessCollector.Collected.Should().HaveCount(0);
        }

        [Test]
        public void Can_reset_empty_many_times()
        {
            WitnessCollector witnessCollector = new(new MemDb(), LimboLogs.Instance);

            using IDisposable tracker = witnessCollector.TrackOnThisThread();
            witnessCollector.Reset();
            witnessCollector.Reset();
            witnessCollector.Reset();

            witnessCollector.Collected.Should().HaveCount(0);
        }

        [Test]
        public void Can_reset_non_empty_many_times()
        {
            WitnessCollector witnessCollector = new(new MemDb(), LimboLogs.Instance);

            using IDisposable tracker = witnessCollector.TrackOnThisThread();
            witnessCollector.Add(TestItem.KeccakA);
            witnessCollector.Reset();
            witnessCollector.Add(TestItem.KeccakA);
            witnessCollector.Reset();
            witnessCollector.Add(TestItem.KeccakA);
            witnessCollector.Reset();

            witnessCollector.Collected.Should().HaveCount(0);
        }

        [Test]
        public void Can_persist_empty()
        {
            IKeyValueStore keyValueStore = new MemDb();

            WitnessCollector witnessCollector = new(keyValueStore, LimboLogs.Instance);
            witnessCollector.Persist(Keccak.Zero);

            var witness = keyValueStore[Keccak.Zero.Bytes];
            witness.Should().BeNull();
        }

        [Test]
        public void Can_persist_more()
        {
            IKeyValueStore keyValueStore = new MemDb();
            WitnessCollector witnessCollector = new(keyValueStore, LimboLogs.Instance);

            using IDisposable tracker = witnessCollector.TrackOnThisThread();
            witnessCollector.Add(TestItem.KeccakA);
            witnessCollector.Add(TestItem.KeccakB);
            witnessCollector.Persist(Keccak.Zero);

            var witness = keyValueStore[Keccak.Zero.Bytes];
            witness.Length.Should().Be(64);
        }

        [Test]
        public void Can_persist_and_load()
        {
            IKeyValueStore keyValueStore = new MemDb();
            WitnessCollector witnessCollector = new(keyValueStore, LimboLogs.Instance);

            using IDisposable tracker = witnessCollector.TrackOnThisThread();
            witnessCollector.Add(TestItem.KeccakA);
            witnessCollector.Add(TestItem.KeccakB);
            witnessCollector.Persist(Keccak.Zero);

            var witness = witnessCollector.Load(Keccak.Zero);
            witness.Should().HaveCount(2);
        }

        [Test]
        public void Can_load_missing()
        {
            IKeyValueStore keyValueStore = new MemDb();
            WitnessCollector witnessCollector = new(keyValueStore, LimboLogs.Instance);
            var witness = witnessCollector.Load(Keccak.Zero);
            witness.Should().BeNull();
        }

        [Test]
        public void Can_read_beyond_cache()
        {
            IKeyValueStore keyValueStore = new MemDb();
            WitnessCollector witnessCollector = new(keyValueStore, LimboLogs.Instance);

            using IDisposable tracker = witnessCollector.TrackOnThisThread();
            for (int i = 0; i < 255; i++)
            {
                witnessCollector.Add(TestItem.Keccaks[i]);
                witnessCollector.Persist(TestItem.Keccaks[i]);
            }

            witnessCollector.Persist(TestItem.KeccakA);
            witnessCollector.Persist(TestItem.KeccakB);

            witnessCollector.Load(TestItem.Keccaks[0]);
        }
    }
}
