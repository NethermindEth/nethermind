// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using FluentAssertions;
using Nethermind.Consensus.Producers;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Producers
{
    [TestFixture]
    public class CompositeBlockProductionTriggerTests
    {
        [Test, Timeout(Timeout.MaxTestTime)]
        public void On_pending_trigger_works()
        {
            int triggered = 0;
            BuildBlocksWhenRequested trigger1 = new();
            BuildBlocksWhenRequested trigger2 = new();
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
