// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Abi;
using Nethermind.Config;
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
        [TestCase(false, 1UL, 4000000UL)]
        [TestCase(true, 1UL, 4000000UL)]
        [TestCase(false, 3UL, 1000UL)]
        [TestCase(false, 5UL, 3000000UL)]
        [TestCase(true, 3UL, 2000000UL)]
        [TestCase(true, 5UL, 3000000UL)]
        [TestCase(true, 10UL, 4000000UL)]
        [TestCase(false, 10UL, 4000000UL)]
        public void GetGasLimit(bool minimum2MlnGasPerBlockWhenUsingBlockGasLimit, ulong blockNumber, ulong? expected)
        {
            IBlockGasLimitContract blockGasLimitContract1 = Substitute.For<IBlockGasLimitContract>();
            blockGasLimitContract1.ActivationBlock.Returns(3UL);
            blockGasLimitContract1.Activation.Returns(3UL);
            blockGasLimitContract1.BlockGasLimit(Arg.Any<BlockHeader>()).Returns(1000u);
            IBlockGasLimitContract blockGasLimitContract2 = Substitute.For<IBlockGasLimitContract>();
            blockGasLimitContract2.ActivationBlock.Returns(5UL);
            blockGasLimitContract2.Activation.Returns(5UL);
            blockGasLimitContract2.BlockGasLimit(Arg.Any<BlockHeader>()).Returns(3000000u);
            IBlockGasLimitContract blockGasLimitContract3 = Substitute.For<IBlockGasLimitContract>();
            blockGasLimitContract3.ActivationBlock.Returns(10UL);
            blockGasLimitContract3.Activation.Returns(10UL);
            blockGasLimitContract3.BlockGasLimit(Arg.Any<BlockHeader>()).Throws(new AbiException(string.Empty));

            BlocksConfig config = new() { TargetBlockGasLimit = 4000000 };
            AuRaContractGasLimitOverride gasLimitOverride = new(
                [blockGasLimitContract1, blockGasLimitContract2, blockGasLimitContract3],
                new AuRaContractGasLimitOverride.Cache(),
                minimum2MlnGasPerBlockWhenUsingBlockGasLimit,
                new TargetAdjustedGasLimitCalculator(MainnetSpecProvider.Instance, config),
                LimboLogs.Instance);

            BlockHeader header = Build.A.BlockHeader.WithGasLimit(3999999).WithNumber(blockNumber - 1).TestObject;

            Assert.That(gasLimitOverride.GetGasLimit(header), Is.EqualTo(expected));
        }
    }
}
