/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    public class ReardCalculatorTests
    {
        [Test]
        public void Two_uncles_from_the_same_coinbase()
        {
            Block ommer = Build.A.Block.WithNumber(1).TestObject;
            Block ommer2 = Build.A.Block.WithNumber(1).TestObject;
            Block block = Build.A.Block.WithNumber(3).WithOmmers(ommer, ommer2).TestObject;
            
            RewardCalculator calculator = new RewardCalculator(RopstenSpecProvider.Instance);
            BlockReward[] rewards = calculator.CalculateRewards(block);
            
            Assert.AreEqual(3, rewards.Length);
        }
    }
}