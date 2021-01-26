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
// 

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
