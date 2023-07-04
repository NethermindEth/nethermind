// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Clique;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Clique.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class WiggleRandomizerTests
    {
        [Test]
        public void Wiggle_is_fine()
        {
            Queue<int> randoms = new(new List<int> { 100, 600, 1000, 2000, 50 });
            ICryptoRandom cryptoRandom = Substitute.For<ICryptoRandom>();
            cryptoRandom.NextInt(Arg.Any<int>()).Returns(ci => randoms.Dequeue());

            Snapshot snapshot = new(1, Keccak.Zero, new SortedList<Address, long>(AddressComparer.Instance)
            {
                {TestItem.AddressA, 1},
                {TestItem.AddressB, 2},
                {TestItem.AddressC, 3},
                {TestItem.AddressD, 4}
            });
            ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
            snapshotManager.GetOrCreateSnapshot(Arg.Any<long>(), Arg.Any<Keccak>()).Returns(snapshot);
            WiggleRandomizer randomizer = new(cryptoRandom, snapshotManager);

            BlockHeader header1 = Build.A.BlockHeader.WithNumber(1).TestObject;
            for (int i = 0; i < 5; i++)
            {
                Assert.That(randomizer.WiggleFor(header1), Is.EqualTo(100));
            }
        }

        [Test]
        public void Wiggle_has_no_min_value()
        {
            Queue<int> randoms = new(new List<int> { Consensus.Clique.Clique.WiggleTime / 2, Consensus.Clique.Clique.WiggleTime, Consensus.Clique.Clique.WiggleTime * 2, Consensus.Clique.Clique.WiggleTime * 3 });
            ICryptoRandom cryptoRandom = Substitute.For<ICryptoRandom>();
            cryptoRandom.NextInt(Arg.Any<int>()).Returns(ci => randoms.Dequeue());

            Snapshot snapshot = new(1, Keccak.Zero, new SortedList<Address, long>(AddressComparer.Instance)
            {
                {TestItem.AddressA, 1},
                {TestItem.AddressB, 2},
                {TestItem.AddressC, 3},
                {TestItem.AddressD, 4}
            });

            ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
            snapshotManager.GetOrCreateSnapshot(Arg.Any<long>(), Arg.Any<Keccak>()).Returns(snapshot);
            WiggleRandomizer randomizer = new(cryptoRandom, snapshotManager);

            BlockHeader header1 = Build.A.BlockHeader.WithNumber(1).TestObject;
            BlockHeader header2 = Build.A.BlockHeader.WithNumber(2).TestObject;
            BlockHeader header3 = Build.A.BlockHeader.WithNumber(3).TestObject;
            int wiggle = randomizer.WiggleFor(header1);
            Assert.That(wiggle, Is.EqualTo(Consensus.Clique.Clique.WiggleTime / 2));

            wiggle = randomizer.WiggleFor(header2);
            Assert.That(wiggle, Is.EqualTo(Consensus.Clique.Clique.WiggleTime));

            wiggle = randomizer.WiggleFor(header3);
            Assert.That(wiggle, Is.EqualTo(Consensus.Clique.Clique.WiggleTime * 2));
        }

        [Test]
        public void Returns_zero_for_in_turn_blocks()
        {
            Queue<int> randoms = new(new List<int> { Consensus.Clique.Clique.WiggleTime / 2, Consensus.Clique.Clique.WiggleTime, Consensus.Clique.Clique.WiggleTime * 2, Consensus.Clique.Clique.WiggleTime * 3 });
            ICryptoRandom cryptoRandom = Substitute.For<ICryptoRandom>();
            cryptoRandom.NextInt(Arg.Any<int>()).Returns(ci => randoms.Dequeue());

            Snapshot snapshot = new(1, Keccak.Zero, new SortedList<Address, long>(AddressComparer.Instance)
            {
                {TestItem.AddressA, 1},
                {TestItem.AddressB, 2},
                {TestItem.AddressC, 3},
                {TestItem.AddressD, 4}
            });

            ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
            snapshotManager.GetOrCreateSnapshot(Arg.Any<long>(), Arg.Any<Keccak>()).Returns(snapshot);
            WiggleRandomizer randomizer = new(cryptoRandom, snapshotManager);

            BlockHeader header1 = Build.A.BlockHeader.WithNumber(1).WithDifficulty(Consensus.Clique.Clique.DifficultyInTurn).TestObject;
            int wiggle = randomizer.WiggleFor(header1);
            Assert.That(wiggle, Is.EqualTo(0));
        }
    }
}
