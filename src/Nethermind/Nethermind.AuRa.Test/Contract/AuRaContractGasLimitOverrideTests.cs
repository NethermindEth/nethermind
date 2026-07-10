// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Abi;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Contract;

public class AuRaContractGasLimitOverrideTests
{
    private const int CorrectHeadGasLimit = 100000000;

    // TestContract:
    // pragma solidity ^0.5.0;
    // contract TestValidatorSet {
    //    function blockGasLimit() public view returns(uint256) {
    //        return 100000000;
    //    }
    // }
    [Test]
    public async Task can_read_block_gas_limit_from_contract()
    {
        using TestGasLimitContractBlockchain chain = await TestContractBlockchain.ForTest<TestGasLimitContractBlockchain, AuRaContractGasLimitOverrideTests>();
        ulong gasLimit = chain.GasLimitCalculator.GetGasLimit(chain.BlockTree.Head.Header);
        Assert.That(gasLimit, Is.EqualTo(CorrectHeadGasLimit));
    }

    [Test]
    public async Task caches_read_block_gas_limit()
    {
        using TestGasLimitContractBlockchain chain = await TestContractBlockchain.ForTest<TestGasLimitContractBlockchain, AuRaContractGasLimitOverrideTests>();
        chain.GasLimitCalculator.GetGasLimit(chain.BlockTree.Head.Header);
        ulong? gasLimit = chain.GasLimitOverrideCache.GasLimitCache.Get(chain.BlockTree.Head.Hash);
        Assert.That(gasLimit, Is.EqualTo(CorrectHeadGasLimit));
    }

    [Test]
    public async Task can_validate_gas_limit_correct()
    {
        using TestGasLimitContractBlockchain chain = await TestContractBlockchain.ForTest<TestGasLimitContractBlockchain, AuRaContractGasLimitOverrideTests>();
        bool isValid = ((AuRaContractGasLimitOverride)chain.GasLimitCalculator).IsGasLimitValid(chain.BlockTree.Head.Header, CorrectHeadGasLimit, out _);
        Assert.That(isValid, Is.True);
    }

    [Test]
    public async Task can_validate_gas_limit_incorrect()
    {
        using TestGasLimitContractBlockchain chain = await TestContractBlockchain.ForTest<TestGasLimitContractBlockchain, AuRaContractGasLimitOverrideTests>();
        bool isValid = ((AuRaContractGasLimitOverride)chain.GasLimitCalculator).IsGasLimitValid(chain.BlockTree.Head.Header, 100000001, out ulong? expectedGasLimit);
        Assert.That(isValid, Is.False);
        Assert.That(expectedGasLimit, Is.EqualTo(CorrectHeadGasLimit));
    }

    [Test]
    public void Override_takes_precedence_over_contract_limit()
    {
        const long innerGasLimit = 99_000_000;
        const long overrideTarget = 50_000_000;
        (AuRaContractGasLimitOverride calculator, IGasLimitCalculator inner) = BuildOverride(contractGasLimit: 42_000_000, innerResult: innerGasLimit);
        BlockHeader parent = Build.A.BlockHeader.WithNumber(10).TestObject;

        ulong result = calculator.GetGasLimit(parent, targetGasLimit: overrideTarget);

        Assert.That(result, Is.EqualTo(innerGasLimit));
        inner.Received(1).GetGasLimit(parent, overrideTarget);
    }

    [Test]
    public void IsGasLimitValid_requires_exact_contract_value()
    {
        const long parentGasLimit = 30_000_000;
        const long contractGasLimit = 42_000_000;
        AuRaContractGasLimitOverride calculator = new(
            [StubContract(contractGasLimit)],
            new AuRaContractGasLimitOverride.Cache(),
            minimum2MlnGasPerBlockWhenUsingBlockGasLimitContract: false,
            new TargetAdjustedGasLimitCalculator(new TestSpecProvider(Prague.Instance), new BlocksConfig()),
            LimboLogs.Instance);
        BlockHeader parent = Build.A.BlockHeader.WithNumber(10).WithGasLimit(parentGasLimit).TestObject;

        bool withinDeltaButNotContract = calculator.IsGasLimitValid(parent, parentGasLimit + 29_000, out ulong? expected);
        bool exactContractValue = calculator.IsGasLimitValid(parent, contractGasLimit, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(withinDeltaButNotContract, Is.False);
            Assert.That(expected, Is.EqualTo(contractGasLimit));
            Assert.That(exactContractValue, Is.True);
        }
    }

    [Test]
    public void Override_is_forwarded_to_inner_when_no_contract_limit()
    {
        const long innerGasLimit = 31_000_000;
        const long overrideTarget = 33_000_000;
        (AuRaContractGasLimitOverride calculator, IGasLimitCalculator inner) = BuildOverride(contractGasLimit: null, innerResult: innerGasLimit);
        BlockHeader parent = Build.A.BlockHeader.WithNumber(10).TestObject;

        ulong result = calculator.GetGasLimit(parent, targetGasLimit: overrideTarget);

        Assert.That(result, Is.EqualTo(innerGasLimit));
        inner.Received(1).GetGasLimit(parent, overrideTarget);
    }

    private static (AuRaContractGasLimitOverride calculator, IGasLimitCalculator inner) BuildOverride(long? contractGasLimit, ulong innerResult)
    {
        IGasLimitCalculator inner = Substitute.For<IGasLimitCalculator>();
        inner.GetGasLimit(Arg.Any<BlockHeader>(), Arg.Any<ulong?>()).Returns(innerResult);

        IBlockGasLimitContract[] contracts = contractGasLimit is { } limit ? [StubContract(limit)] : [];
        AuRaContractGasLimitOverride calculator = new(
            contracts,
            new AuRaContractGasLimitOverride.Cache(),
            minimum2MlnGasPerBlockWhenUsingBlockGasLimitContract: false,
            inner,
            LimboLogs.Instance);
        return (calculator, inner);
    }

    private static IBlockGasLimitContract StubContract(long gasLimit)
    {
        IBlockGasLimitContract contract = Substitute.For<IBlockGasLimitContract>();
        contract.ActivationBlock.Returns(0UL);
        contract.BlockGasLimit(Arg.Any<BlockHeader>()).Returns((UInt256)gasLimit);
        return contract;
    }

    [Test]
    public async Task skip_validate_gas_limit_before_enabled()
    {
        using TestGasLimitContractBlockchainLateBlockGasLimit chain = await TestContractBlockchain.ForTest<TestGasLimitContractBlockchainLateBlockGasLimit, AuRaContractGasLimitOverrideTests>();
        bool isValid = ((AuRaContractGasLimitOverride)chain.GasLimitCalculator).IsGasLimitValid(chain.BlockTree.Genesis, 100000001, out _);
        Assert.That(isValid, Is.True);
    }

    public class TestGasLimitContractBlockchain : TestContractBlockchain
    {
        public AuRaContractGasLimitOverride GasLimitCalculator => Container.Resolve<AuRaContractGasLimitOverride>();
        public AuRaContractGasLimitOverride.Cache GasLimitOverrideCache => Container.Resolve<AuRaContractGasLimitOverride.Cache>();

        private AuRaContractGasLimitOverride CreateGasLimitCalculator(
            ChainSpec chainSpec,
            AuRaContractGasLimitOverride.Cache gasLimitOverrideCache,
            ISpecProvider specProvider,
            IReadOnlyTxProcessingEnvFactory txProcessingEnvFactory
        )
        {
            KeyValuePair<ulong, Address> blockGasLimitContractTransition = chainSpec.EngineChainSpecParametersProvider
                .GetChainSpecParameters<AuRaChainSpecEngineParameters>().BlockGasLimitContractTransitions
                .First();
            BlockGasLimitContract gasLimitContract = new(AbiEncoder.Instance, blockGasLimitContractTransition.Value,
                blockGasLimitContractTransition.Key,
                txProcessingEnvFactory.Create());

            return new AuRaContractGasLimitOverride(new[] { gasLimitContract }, gasLimitOverrideCache, false, new FollowOtherMiners(specProvider), LimboLogs.Instance);
        }

        protected override ContainerBuilder ConfigureContainer(ContainerBuilder builder, IConfigProvider configProvider) =>
            base.ConfigureContainer(builder, configProvider)
                .AddModule(new AuRaModule(CreateChainSpec()))
                .AddScoped<IBlockProcessor, BlockProcessor>()
                .AddSingleton<AuRaContractGasLimitOverride, ChainSpec, AuRaContractGasLimitOverride.Cache, ISpecProvider, IReadOnlyTxProcessingEnvFactory>(CreateGasLimitCalculator);

        protected override Task AddBlocksOnStart() => Task.CompletedTask;
    }

    public class TestGasLimitContractBlockchainLateBlockGasLimit : TestGasLimitContractBlockchain
    {
        protected override ChainSpec CreateChainSpec()
        {
            ChainSpec chainSpec = base.CreateChainSpec();
            AuRaChainSpecEngineParameters parameters = chainSpec.EngineChainSpecParametersProvider
                .GetChainSpecParameters<AuRaChainSpecEngineParameters>();
            KeyValuePair<ulong, Address> blockGasLimitContractTransition = parameters.BlockGasLimitContractTransitions.First();
            parameters.BlockGasLimitContractTransitions = new Dictionary<ulong, Address>() { { 10, blockGasLimitContractTransition.Value } };
            return chainSpec;
        }
    }
}
