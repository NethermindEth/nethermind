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

using FluentAssertions;
using Nethermind.Blockchain.Producers;
using Nethermind.Core;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Producers
{
    public class IfPoolIsNotEmptyTests
    {
        [TestCase(0, false)]
        [TestCase(1, true)]
        public void Does_not_trigger_when_empty(int txCount, bool shouldTrigger)
        {
            var pool = Substitute.For<ITxPool>();
            pool.GetPendingTransactionsCount().Returns(txCount);
            bool triggered = false;
            BuildBlocksWhenRequested trigger = new BuildBlocksWhenRequested();
            IBlockProductionTrigger withCondition = trigger.IfPoolIsNotEmpty(pool);
            withCondition.TriggerBlockProduction += (s, e) => triggered = true;
            trigger.BuildBlock();
            triggered.Should().Be(shouldTrigger);
        }
    }
}
