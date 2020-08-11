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
using Nethermind.Consensus;
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
            var gasLimit = chain.GasLimitCalculator.GetGasLimit(chain.BlockTree.Head.Header);
            gasLimit.Should().Be(100000000);
        }

        public class TestGasLimitContractBlockchain : TestContractBlockchain
        {
            public IGasLimitCalculator GasLimitCalculator { get; private set; }
            public AuRaContractGasLimitCalculator.Cache GasLimitOverrideCache { get; private set; }
            
            protected override BlockProcessor CreateBlockProcessor()
            {
                var validator = new AuRaParameters.Validator()
                {
                    Addresses = TestItem.Addresses,
                    ValidatorType = AuRaParameters.ValidatorType.List
                };

                var blockGasLimitContractTransition = this.ChainSpec.AuRa.BlockGasLimitContractTransitions.First();
                var gasLimitContract = new BlockGasLimitContract(new AbiEncoder(), blockGasLimitContractTransition.Value, blockGasLimitContractTransition.Key,
                    new ReadOnlyTxProcessorSource(DbProvider, BlockTree, SpecProvider, LimboLogs.Instance));
                
                GasLimitOverrideCache = new AuRaContractGasLimitCalculator.Cache();
                GasLimitCalculator = new AuRaContractGasLimitCalculator(new[] {gasLimitContract}, GasLimitOverrideCache, false, FollowOtherMiners.Instance, LimboLogs.Instance);

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
                    GasLimitCalculator);
            }

            protected override Task AddBlocksOnStart() => Task.CompletedTask;
        }
    }
}
