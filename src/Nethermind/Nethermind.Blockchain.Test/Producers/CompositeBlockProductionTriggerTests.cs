//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Threading;
using FluentAssertions;
using Nethermind.Blockchain.Producers;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Producers
{
    [TestFixture]
    public class CompositeBlockProductionTriggerTests
    {
        [Test]
        public void On_pending_trigger_works()
        {
            int triggered = 0;
            BuildBlocksWhenRequested trigger1 = new BuildBlocksWhenRequested();
            BuildBlocksWhenRequested trigger2 = new BuildBlocksWhenRequested();
            IBlockProductionTrigger composite = trigger1.Or(trigger2);
            composite.TriggerBlockProduction += (s, e) => triggered++;
            trigger1.BuildBlock();
            trigger2.BuildBlock();
            trigger1.BuildBlock();
            trigger2.BuildBlock();

            triggered.Should().Be(4);
        }
    }
}
