// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Rewards
{
    public class NoBlockRewardsTests
    {
        [Test, Timeout(Timeout.MaxTestTime)]
        public void No_rewards()
        {
            Block block = Build.A.Block.WithNumber(10).WithUncles(Build.A.Block.WithNumber(9).TestObject).TestObject;
            NoBlockRewards calculator = NoBlockRewards.Instance;
            var rewards = calculator.CalculateRewards(block, Substitute.For<IWorldState>());
            Assert.IsEmpty(rewards);
        }
    }
}
