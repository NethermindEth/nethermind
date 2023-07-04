// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
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
            var rewards = calculator.CalculateRewards(block);
            Assert.IsEmpty(rewards);
        }
    }
}
