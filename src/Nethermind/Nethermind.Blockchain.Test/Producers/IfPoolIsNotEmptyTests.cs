// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Consensus.Producers;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Producers
{
    public class IfPoolIsNotEmptyTests
    {
        [Timeout(Timeout.MaxTestTime)]
        [TestCase(0, false)]
        [TestCase(1, true)]
        public void Does_not_trigger_when_empty(int txCount, bool shouldTrigger)
        {
            var pool = Substitute.For<ITxPool>();
            pool.GetPendingTransactionsCount().Returns(txCount);
            bool triggered = false;
            BuildBlocksWhenRequested trigger = new();
            IBlockProductionTrigger withCondition = trigger.IfPoolIsNotEmpty(pool);
            withCondition.TriggerBlockProduction += (s, e) => triggered = true;
            trigger.BuildBlock();
            triggered.Should().Be(shouldTrigger);
        }
    }
}
