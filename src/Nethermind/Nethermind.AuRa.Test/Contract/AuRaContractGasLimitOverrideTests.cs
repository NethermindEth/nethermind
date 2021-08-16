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

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Contract
{
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
            using var chain = await TestContractBlockchain.ForTest<TestGasLimitContractBlockchain, AuRaContractGasLimitOverrideTests>();
            var gasLimit = chain.GasLimitCalculator.GetGasLimit(chain.BlockTree.Head.Header);
            gasLimit.Should().Be(CorrectHeadGasLimit);
        }
        
        [Test]
        public async Task caches_read_block_gas_limit()
        {
            using var chain = await TestContractBlockchain.ForTest<TestGasLimitContractBlockchain, AuRaContractGasLimitOverrideTests>();
            chain.GasLimitCalculator.GetGasLimit(chain.BlockTree.Head.Header);
            var gasLimit = chain.GasLimitOverrideCache.GasLimitCache.Get(chain.BlockTree.Head.Hash);
            gasLimit.Should().Be(CorrectHeadGasLimit);
        }
        
        [Test]
        public async Task can_validate_gas_limit_correct()
        {
            using var chain = await TestContractBlockchain.ForTest<TestGasLimitContractBlockchain, AuRaContractGasLimitOverrideTests>();
            var isValid = ((AuRaContractGasLimitOverride) chain.GasLimitCalculator).IsGasLimitValid(chain.BlockTree.Head.Header, CorrectHeadGasLimit, out _);
            isValid.Should().BeTrue();
        }
        
        [Test]
        public async Task can_validate_gas_limit_incorrect()
        {
            using var chain = await TestContractBlockchain.ForTest<TestGasLimitContractBlockchain, AuRaContractGasLimitOverrideTests>();
            var isValid = ((AuRaContractGasLimitOverride) chain.GasLimitCalculator).IsGasLimitValid(chain.BlockTree.Head.Header, 100000001, out long? expectedGasLimit);
            isValid.Should().BeFalse();
            expectedGasLimit.Should().Be(CorrectHeadGasLimit);
        }
        
        [Test]
        public async Task skip_validate_gas_limit_before_enabled()
        {
            using var chain = await TestContractBlockchain.ForTest<TestGasLimitContractBlockchainLateBlockGasLimit, AuRaContractGasLimitOverrideTests>();
            var isValid = ((AuRaContractGasLimitOverride) chain.GasLimitCalculator).IsGasLimitValid(chain.BlockTree.Genesis, 100000001, out _);
            isValid.Should().BeTrue();
        }

        public class TestGasLimitContractBlockchain : TestContractBlockchain
        {
            public IGasLimitCalculator GasLimitCalculator { get; private set; }
            public AuRaContractGasLimitOverride.Cache GasLimitOverrideCache { get; private set; }
            
            protected override BlockProcessor CreateBlockProcessor()
            {
                var blockGasLimitContractTransition = ChainSpec.AuRa.BlockGasLimitContractTransitions.First();
                var gasLimitContract = new BlockGasLimitContract(AbiEncoder.Instance, blockGasLimitContractTransition.Value, blockGasLimitContractTransition.Key,
                    new ReadOnlyTxProcessingEnv(
                        DbProvider,
                        new TrieStore(DbProvider.StateDb, LimboLogs.Instance).AsReadOnly(),
                        BlockTree, SpecProvider, LimboLogs.Instance));
                
                GasLimitOverrideCache = new AuRaContractGasLimitOverride.Cache();
                GasLimitCalculator = new AuRaContractGasLimitOverride(new[] {gasLimitContract}, GasLimitOverrideCache, false, new FollowOtherMiners(SpecProvider), LimboLogs.Instance);

                return new AuRaBlockProcessor(
                    SpecProvider,
                    Always.Valid,
                    new RewardCalculator(SpecProvider),
                    new BlockProcessor.BlockValidationTransactionsExecutor(TxProcessor, State),
                    State,
                    Storage,
                    ReceiptStorage,
                    LimboLogs.Instance,
                    BlockTree,
                    null,
                    GasLimitCalculator as AuRaContractGasLimitOverride);
            }

            protected override Task AddBlocksOnStart() => Task.CompletedTask;
        }
        
        public class TestGasLimitContractBlockchainLateBlockGasLimit : TestGasLimitContractBlockchain
        {
            protected override BlockProcessor CreateBlockProcessor()
            {
                var blockGasLimitContractTransition = ChainSpec.AuRa.BlockGasLimitContractTransitions.First();
                ChainSpec.AuRa.BlockGasLimitContractTransitions = new Dictionary<long, Address>() {{10, blockGasLimitContractTransition.Value}};
                return base.CreateBlockProcessor();
            }
        }
    }
}
