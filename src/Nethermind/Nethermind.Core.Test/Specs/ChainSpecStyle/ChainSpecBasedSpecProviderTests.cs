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

using Nethermind.Core.Specs.ChainSpecStyle;
using NUnit.Framework;

namespace Nethermind.Core.Test.Specs.ChainSpecStyle
{
    [TestFixture]
    public class ChainSpecBasedSpecProviderTests
    {
        [Test]
        public void Max_code_transition_loaded_correctly()
        {
            const long maxCodeTransition = 13;
            const long maxCodeSize = 100;

            ChainSpec chainSpec = new ChainSpec();
            chainSpec.Parameters = new ChainParameters();
            chainSpec.Parameters.MaxCodeSizeTransition = maxCodeTransition;
            chainSpec.Parameters.MaxCodeSize = maxCodeSize;

            ChainSpecBasedSpecProvider provider = new ChainSpecBasedSpecProvider(chainSpec);
            Assert.AreEqual(long.MaxValue, provider.GetSpec(maxCodeTransition - 1).MaxCodeSize, "one before");
            Assert.AreEqual(maxCodeSize, provider.GetSpec(maxCodeTransition).MaxCodeSize, "at transition");
            Assert.AreEqual(maxCodeSize, provider.GetSpec(maxCodeTransition + 1).MaxCodeSize, "one after");
        }

        [Test]
        public void Eip_transitions_loaded_correctly()
        {
            const long maxCodeTransition = 1;
            const long maxCodeSize = 1;

            ChainSpec chainSpec = new ChainSpec();
            chainSpec.Parameters = new ChainParameters();
            chainSpec.Parameters.MaxCodeSizeTransition = maxCodeTransition;
            chainSpec.Parameters.MaxCodeSize = maxCodeSize;

            chainSpec.Parameters.Eip140Transition = 1400L;
            chainSpec.Parameters.Eip145Transition = 1450L;
            chainSpec.Parameters.Eip150Transition = 1500L;
            chainSpec.Parameters.Eip155Transition = 1550L;
            chainSpec.Parameters.Eip160Transition = 1600L;
            chainSpec.Parameters.Eip161abcTransition = 1610L;
            chainSpec.Parameters.Eip161dTransition = 1611L;
            chainSpec.Parameters.Eip211Transition = 2110L;
            chainSpec.Parameters.Eip214Transition = 2140L;
            chainSpec.Parameters.Eip658Transition = 6580L;
            chainSpec.Parameters.Eip1014Transition = 10140L;
            chainSpec.Parameters.Eip1052Transition = 10520L;
            chainSpec.Parameters.Eip1283Transition = 12830L;
            
            ChainSpecBasedSpecProvider provider = new ChainSpecBasedSpecProvider(chainSpec);
            Assert.AreEqual(long.MaxValue, provider.GetSpec(maxCodeTransition - 1).MaxCodeSize, "one before");
            Assert.AreEqual(maxCodeSize, provider.GetSpec(maxCodeTransition).MaxCodeSize, "at transition");
            Assert.AreEqual(maxCodeSize, provider.GetSpec(maxCodeTransition + 1).MaxCodeSize, "one after");
            
            
        }
    }
}