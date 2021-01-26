//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
            Queue<int> randoms = new Queue<int>(new List<int> {100, 600, 1000, 2000, 50});
            ICryptoRandom cryptoRandom = Substitute.For<ICryptoRandom>();
            cryptoRandom.NextInt(Arg.Any<int>()).Returns(ci => randoms.Dequeue());

            Snapshot snapshot = new Snapshot(1, Keccak.Zero, new SortedList<Address, long>(AddressComparer.Instance)
            {
                {TestItem.AddressA, 1},
                {TestItem.AddressB, 2},
                {TestItem.AddressC, 3},
                {TestItem.AddressD, 4}
            });
            ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
            snapshotManager.GetOrCreateSnapshot(Arg.Any<long>(), Arg.Any<Keccak>()).Returns(snapshot);
            WiggleRandomizer randomizer = new WiggleRandomizer(cryptoRandom, snapshotManager);

            BlockHeader header1 = Build.A.BlockHeader.WithNumber(1).TestObject;
            for (int i = 0; i < 5; i++)
            {
                Assert.AreEqual(100, randomizer.WiggleFor(header1));
            }
        }

        [Test]
        public void Wiggle_has_no_min_value()
        {
            Queue<int> randoms = new Queue<int>(new List<int> {Consensus.Clique.Clique.WiggleTime / 2, Consensus.Clique.Clique.WiggleTime, Consensus.Clique.Clique.WiggleTime * 2, Consensus.Clique.Clique.WiggleTime * 3});
            ICryptoRandom cryptoRandom = Substitute.For<ICryptoRandom>();
            cryptoRandom.NextInt(Arg.Any<int>()).Returns(ci => randoms.Dequeue());

            Snapshot snapshot = new Snapshot(1, Keccak.Zero, new SortedList<Address, long>(AddressComparer.Instance)
            {
                {TestItem.AddressA, 1},
                {TestItem.AddressB, 2},
                {TestItem.AddressC, 3},
                {TestItem.AddressD, 4}
            });
            
            ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
            snapshotManager.GetOrCreateSnapshot(Arg.Any<long>(), Arg.Any<Keccak>()).Returns(snapshot);
            WiggleRandomizer randomizer = new WiggleRandomizer(cryptoRandom, snapshotManager);

            BlockHeader header1 = Build.A.BlockHeader.WithNumber(1).TestObject;
            BlockHeader header2 = Build.A.BlockHeader.WithNumber(2).TestObject;
            BlockHeader header3 = Build.A.BlockHeader.WithNumber(3).TestObject;
            int wiggle = randomizer.WiggleFor(header1);
            Assert.AreEqual(Consensus.Clique.Clique.WiggleTime / 2, wiggle);
            
            wiggle = randomizer.WiggleFor(header2);
            Assert.AreEqual(Consensus.Clique.Clique.WiggleTime, wiggle);
            
            wiggle = randomizer.WiggleFor(header3);
            Assert.AreEqual(Consensus.Clique.Clique.WiggleTime * 2, wiggle);
        }
        
        [Test]
        public void Returns_zero_for_in_turn_blocks()
        {
            Queue<int> randoms = new Queue<int>(new List<int> {Consensus.Clique.Clique.WiggleTime / 2, Consensus.Clique.Clique.WiggleTime, Consensus.Clique.Clique.WiggleTime * 2, Consensus.Clique.Clique.WiggleTime * 3});
            ICryptoRandom cryptoRandom = Substitute.For<ICryptoRandom>();
            cryptoRandom.NextInt(Arg.Any<int>()).Returns(ci => randoms.Dequeue());

            Snapshot snapshot = new Snapshot(1, Keccak.Zero, new SortedList<Address, long>(AddressComparer.Instance)
            {
                {TestItem.AddressA, 1},
                {TestItem.AddressB, 2},
                {TestItem.AddressC, 3},
                {TestItem.AddressD, 4}
            });
            
            ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
            snapshotManager.GetOrCreateSnapshot(Arg.Any<long>(), Arg.Any<Keccak>()).Returns(snapshot);
            WiggleRandomizer randomizer = new WiggleRandomizer(cryptoRandom, snapshotManager);

            BlockHeader header1 = Build.A.BlockHeader.WithNumber(1).WithDifficulty(Consensus.Clique.Clique.DifficultyInTurn).TestObject;
            int wiggle = randomizer.WiggleFor(header1);
            Assert.AreEqual(0, wiggle);
        }
    }
}
