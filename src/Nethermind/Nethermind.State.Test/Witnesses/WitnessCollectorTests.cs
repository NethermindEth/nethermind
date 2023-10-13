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
            witnessCollector.Add(Commitment.Zero);
            witnessCollector.Add(Commitment.Zero);

            witnessCollector.Collected.Should().HaveCount(1);
        }

        [Test]
        public void Can_collect_many()
        {
            WitnessCollector witnessCollector = new(new MemDb(), LimboLogs.Instance);

            using IDisposable tracker = witnessCollector.TrackOnThisThread();
            witnessCollector.Add(TestItem._commitmentA);
            witnessCollector.Add(TestItem._commitmentB);

            witnessCollector.Collected.Should().HaveCount(2);
        }

        [Test]
        public void Can_reset()
        {
            WitnessCollector witnessCollector = new(new MemDb(), LimboLogs.Instance);

            using IDisposable tracker = witnessCollector.TrackOnThisThread();
            witnessCollector.Add(TestItem._commitmentA);
            witnessCollector.Add(TestItem._commitmentB);
            witnessCollector.Reset();

            witnessCollector.Collected.Should().HaveCount(0);
        }

        [Test]
        public void Can_collect_after_reset()
        {
            WitnessCollector witnessCollector = new(new MemDb(), LimboLogs.Instance);

            using IDisposable tracker = witnessCollector.TrackOnThisThread();
            witnessCollector.Add(TestItem._commitmentA);
            witnessCollector.Add(TestItem._commitmentB);
            witnessCollector.Reset();
            witnessCollector.Add(TestItem._commitmentC);

            witnessCollector.Collected.Should().HaveCount(1);
        }

        [Test]
        public void Collects_what_it_should_collect()
        {
            WitnessCollector witnessCollector = new(new MemDb(), LimboLogs.Instance);

            using IDisposable tracker = witnessCollector.TrackOnThisThread();
            witnessCollector.Add(TestItem._commitmentA);
            witnessCollector.Add(TestItem._commitmentB);

            witnessCollector.Collected.Should().Contain(TestItem._commitmentA);
            witnessCollector.Collected.Should().Contain(TestItem._commitmentB);
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
            witnessCollector.Add(TestItem._commitmentA);
            witnessCollector.Reset();
            witnessCollector.Add(TestItem._commitmentA);
            witnessCollector.Reset();
            witnessCollector.Add(TestItem._commitmentA);
            witnessCollector.Reset();

            witnessCollector.Collected.Should().HaveCount(0);
        }

        [Test]
        public void Can_persist_empty()
        {
            IKeyValueStore keyValueStore = new MemDb();

            WitnessCollector witnessCollector = new(keyValueStore, LimboLogs.Instance);
            witnessCollector.Persist(Commitment.Zero);

            var witness = keyValueStore[Commitment.Zero.Bytes];
            witness.Should().BeNull();
        }

        [Test]
        public void Can_persist_more()
        {
            IKeyValueStore keyValueStore = new MemDb();
            WitnessCollector witnessCollector = new(keyValueStore, LimboLogs.Instance);

            using IDisposable tracker = witnessCollector.TrackOnThisThread();
            witnessCollector.Add(TestItem._commitmentA);
            witnessCollector.Add(TestItem._commitmentB);
            witnessCollector.Persist(Commitment.Zero);

            var witness = keyValueStore[Commitment.Zero.Bytes];
            witness.Length.Should().Be(64);
        }

        [Test]
        public void Can_persist_and_load()
        {
            IKeyValueStore keyValueStore = new MemDb();
            WitnessCollector witnessCollector = new(keyValueStore, LimboLogs.Instance);

            using IDisposable tracker = witnessCollector.TrackOnThisThread();
            witnessCollector.Add(TestItem._commitmentA);
            witnessCollector.Add(TestItem._commitmentB);
            witnessCollector.Persist(Commitment.Zero);

            var witness = witnessCollector.Load(Commitment.Zero);
            witness.Should().HaveCount(2);
        }

        [Test]
        public void Can_load_missing()
        {
            IKeyValueStore keyValueStore = new MemDb();
            WitnessCollector witnessCollector = new(keyValueStore, LimboLogs.Instance);
            var witness = witnessCollector.Load(Commitment.Zero);
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

            witnessCollector.Persist(TestItem._commitmentA);
            witnessCollector.Persist(TestItem._commitmentB);

            witnessCollector.Load(TestItem.Keccaks[0]);
        }
    }
}
