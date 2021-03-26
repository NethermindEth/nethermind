//  Copyright (c) 2021 Demerzel Solutions Limited
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

using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Specs;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;

namespace Nethermind.AuRa.Test
{
    public class AuRaContractGasLimitOverrideTests
    {
        [TestCase(false, 1, 4000000)]
        [TestCase(true, 1, 4000000)]
        [TestCase(false, 3, 1000)]
        [TestCase(false, 5, 3000000)]
        [TestCase(true, 3, 2000000)]
        [TestCase(true, 5, 3000000)]
        [TestCase(true, 10, 4000000)]
        [TestCase(false, 10, 4000000)]
        public void GetGasLimit(bool minimum2MlnGasPerBlockWhenUsingBlockGasLimit, long blockNumber, long? expected)
        {
            var blockGasLimitContract1 = Substitute.For<IBlockGasLimitContract>();
            blockGasLimitContract1.ActivationBlock.Returns(3);
            blockGasLimitContract1.Activation.Returns(3);
            blockGasLimitContract1.BlockGasLimit(Arg.Any<BlockHeader>()).Returns(1000u);
            var blockGasLimitContract2 = Substitute.For<IBlockGasLimitContract>();
            blockGasLimitContract2.ActivationBlock.Returns(5);
            blockGasLimitContract2.Activation.Returns(5);
            blockGasLimitContract2.BlockGasLimit(Arg.Any<BlockHeader>()).Returns(3000000u);
            var blockGasLimitContract3 = Substitute.For<IBlockGasLimitContract>();
            blockGasLimitContract3.ActivationBlock.Returns(10);
            blockGasLimitContract3.Activation.Returns(10);
            blockGasLimitContract3.BlockGasLimit(Arg.Any<BlockHeader>()).Throws(new AbiException(string.Empty));

            var config = new MiningConfig {TargetBlockGasLimit = 4000000};
            var gasLimitOverride = new AuRaContractGasLimitOverride(
                new List<IBlockGasLimitContract> {blockGasLimitContract1, blockGasLimitContract2, blockGasLimitContract3}, 
                new AuRaContractGasLimitOverride.Cache(), 
                minimum2MlnGasPerBlockWhenUsingBlockGasLimit,
                new TargetAdjustedGasLimitCalculator(MainnetSpecProvider.Instance, config), 
                LimboLogs.Instance);

            var header = Build.A.BlockHeader.WithGasLimit(3999999).WithNumber(blockNumber - 1).TestObject;
            
            gasLimitOverride.GetGasLimit(header).Should().Be(expected);
        }
    }
}
