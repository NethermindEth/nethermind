// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
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
        long gasLimit = chain.GasLimitCalculator.GetGasLimit(chain.BlockTree.Head.Header);
        gasLimit.Should().Be(CorrectHeadGasLimit);
    }

    [Test]
    public async Task caches_read_block_gas_limit()
    {
        using TestGasLimitContractBlockchain chain = await TestContractBlockchain.ForTest<TestGasLimitContractBlockchain, AuRaContractGasLimitOverrideTests>();
        chain.GasLimitCalculator.GetGasLimit(chain.BlockTree.Head.Header);
        long? gasLimit = chain.GasLimitOverrideCache.GasLimitCache.Get(chain.BlockTree.Head.Hash);
        gasLimit.Should().Be(CorrectHeadGasLimit);
    }

    [Test]
    public async Task can_validate_gas_limit_correct()
    {
        using TestGasLimitContractBlockchain chain = await TestContractBlockchain.ForTest<TestGasLimitContractBlockchain, AuRaContractGasLimitOverrideTests>();
        bool isValid = ((AuRaContractGasLimitOverride)chain.GasLimitCalculator).IsGasLimitValid(chain.BlockTree.Head.Header, CorrectHeadGasLimit, out _);
        isValid.Should().BeTrue();
    }

    [Test]
    public async Task can_validate_gas_limit_incorrect()
    {
        using TestGasLimitContractBlockchain chain = await TestContractBlockchain.ForTest<TestGasLimitContractBlockchain, AuRaContractGasLimitOverrideTests>();
        bool isValid = ((AuRaContractGasLimitOverride)chain.GasLimitCalculator).IsGasLimitValid(chain.BlockTree.Head.Header, 100000001, out long? expectedGasLimit);
        isValid.Should().BeFalse();
        expectedGasLimit.Should().Be(CorrectHeadGasLimit);
    }

    [Test]
    public async Task skip_validate_gas_limit_before_enabled()
    {
        using TestGasLimitContractBlockchainLateBlockGasLimit chain = await TestContractBlockchain.ForTest<TestGasLimitContractBlockchainLateBlockGasLimit, AuRaContractGasLimitOverrideTests>();
        bool isValid = ((AuRaContractGasLimitOverride)chain.GasLimitCalculator).IsGasLimitValid(chain.BlockTree.Genesis, 100000001, out _);
        isValid.Should().BeTrue();
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
            KeyValuePair<long, Address> blockGasLimitContractTransition = chainSpec.EngineChainSpecParametersProvider
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
            var parameters = chainSpec.EngineChainSpecParametersProvider
                .GetChainSpecParameters<AuRaChainSpecEngineParameters>();
            KeyValuePair<long, Address> blockGasLimitContractTransition = parameters.BlockGasLimitContractTransitions.First();
            parameters.BlockGasLimitContractTransitions = new Dictionary<long, Address>() { { 10, blockGasLimitContractTransition.Value } };
            return chainSpec;
        }
    }
}
