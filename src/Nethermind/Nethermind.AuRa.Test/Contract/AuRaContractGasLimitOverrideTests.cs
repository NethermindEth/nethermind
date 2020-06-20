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

using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Contract
{
    public class AuRaContractGasLimitOverrideTests
    {
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
            var chain = await TestContractBlockchain.ForTest<TestGasLimitContractBlockchain, AuRaContractGasLimitOverrideTests>();
            var gasLimit = chain.GasLimitOverride.GetGasLimit(chain.BlockTree.Head.Header);
            gasLimit.Should().Be(100000000);
        }

        public class TestGasLimitContractBlockchain : TestContractBlockchain
        {
            public IGasLimitOverride GasLimitOverride { get; private set; }
            public IGasLimitOverride.Cache GasLimitOverrideCache { get; private set; }
            
            protected override BlockProcessor CreateBlockProcessor()
            {
                var validator = new AuRaParameters.Validator()
                {
                    Addresses = TestItem.Addresses,
                    ValidatorType = AuRaParameters.ValidatorType.List
                };

                var blockGasLimitContractTransition = this.ChainSpec.AuRa.BlockGasLimitContractTransitions.First();
                var gasLimitContract = new BlockGasLimitContract(new AbiEncoder(), blockGasLimitContractTransition.Value, blockGasLimitContractTransition.Key,
                    new ReadOnlyTransactionProcessorSource(DbProvider, BlockTree, SpecProvider, LimboLogs.Instance));
                
                GasLimitOverrideCache = new IGasLimitOverride.Cache();
                GasLimitOverride = new AuRaContractGasLimitOverride(new[] {gasLimitContract}, GasLimitOverrideCache, false, LimboLogs.Instance);

                return new AuRaBlockProcessor(
                    SpecProvider,
                    Always.Valid,
                    new RewardCalculator(SpecProvider),
                    TxProcessor,
                    StateDb,
                    CodeDb,
                    State,
                    Storage,
                    TxPool,
                    ReceiptStorage,
                    LimboLogs.Instance,
                    BlockTree,
                    null,
                    GasLimitOverride);
            }

            protected override Task AddBlocksOnStart() => Task.CompletedTask;
        }
    }
}
