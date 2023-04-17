// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Producers
{
    [TestFixture]
    public class BuildBlocksWhenRequestedTests
    {
        [Test, Timeout(Timeout.MaxTestTime)]
        public void Manual_trigger_works()
        {
            bool triggered = false;
            BuildBlocksWhenRequested trigger = new();
            trigger.TriggerBlockProduction += (s, e) => triggered = true;
            trigger.BuildBlock();
            triggered.Should().BeTrue();
        }
    }
}
