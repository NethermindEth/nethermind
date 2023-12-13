// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Test.Builders;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Producers
{
    [TestFixture]
    public class BuildBlockOnEachPendingTxTests
    {
        [Test, Timeout(Timeout.MaxTestTime)]
        public void On_pending_trigger_works()
        {
            int triggered = 0;
            ITxPool txPool = Substitute.For<ITxPool>();
            BuildBlockOnEachPendingTx trigger = new(txPool);
            trigger.TriggerBlockProduction += (s, e) => triggered++;
            for (int i = 0; i < 2; i++)
            {
                txPool.NewPending += Raise.EventWith(new TxEventArgs(Build.A.Transaction.TestObject));
            }

            triggered.Should().Be(2);
        }
    }
}
