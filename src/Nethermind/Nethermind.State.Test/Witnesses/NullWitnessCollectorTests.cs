// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Store.Test.Witnesses
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class NullWitnessCollectorTests
    {
        [Test]
        public void Cannot_call_add()
        {
            Assert.Throws<InvalidOperationException>(
                () => NullWitnessCollector.Instance.Add(Keccak.Zero));
        }

        [Test]
        public void Collected_is_empty()
        {
            NullWitnessCollector.Instance.Collected.Should().HaveCount(0);
        }

        [Test]
        public void Reset_does_nothing()
        {
            NullWitnessCollector.Instance.Reset();
            NullWitnessCollector.Instance.Reset();
        }

        [Test]
        public void Persist_does_nothing()
        {
            NullWitnessCollector.Instance.Persist(Keccak.Zero);
        }

        [Test]
        public void Load_throws()
        {
            Assert.Throws<InvalidOperationException>(
                () => NullWitnessCollector.Instance.Load(Keccak.Zero));
        }
    }
}
