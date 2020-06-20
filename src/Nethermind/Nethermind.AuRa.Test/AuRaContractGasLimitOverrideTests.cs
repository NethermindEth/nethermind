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

using System.Collections.Generic;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test
{
    public class AuRaContractGasLimitOverrideTests
    {
        [TestCase(false, 1, ExpectedResult = null)]
        [TestCase(true, 1, ExpectedResult = null)]
        [TestCase(false, 3, ExpectedResult = 1000)]
        [TestCase(false, 5, ExpectedResult = 3000000)]
        [TestCase(true, 3, ExpectedResult = 2000000)]
        [TestCase(true, 5, ExpectedResult = 3000000)]
        public long? GetGasLimit(bool minimum2MlnGasPerBlockWhenUsingBlockGasLimit, long blockNumber)
        {
            var blockGasLimitContract1 = Substitute.For<IBlockGasLimitContract>();
            blockGasLimitContract1.ActivationBlock.Returns(3);
            blockGasLimitContract1.Activation.Returns(3);
            blockGasLimitContract1.BlockGasLimit(Arg.Any<BlockHeader>()).Returns(1000u);
            var blockGasLimitContract2 = Substitute.For<IBlockGasLimitContract>();
            blockGasLimitContract2.ActivationBlock.Returns(5);
            blockGasLimitContract2.Activation.Returns(5);
            blockGasLimitContract2.BlockGasLimit(Arg.Any<BlockHeader>()).Returns(3000000u);
            
            var gasLimitOverride = new AuRaContractGasLimitOverride(
                new List<IBlockGasLimitContract>() {blockGasLimitContract1, blockGasLimitContract2}, 
                new IGasLimitOverride.Cache(), 
                minimum2MlnGasPerBlockWhenUsingBlockGasLimit, 
                LimboLogs.Instance);

            var header = Build.A.BlockHeader.WithNumber(blockNumber - 1).TestObject;
            return gasLimitOverride.GetGasLimit(header);
        }
    }
}