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

using System;
using System.Linq;
using Nethermind.Abi;
using Nethermind.AuRa.Contracts;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Rewards;
using Nethermind.Core;
using Nethermind.Core.Specs.ChainSpecStyle;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;

namespace Nethermind.AuRa.Rewards
{
    public class AuRaRewardCalculator : IRewardCalculator
    {
        private readonly long _blockRewardContractTransition;
        private readonly Address _blockRewardContractAddress;
        private readonly StaticRewardCalculator _blockRewardCalculator;
        private readonly RewardContract _contract;
        private readonly CallOutputTracer _tracer = new CallOutputTracer();

        public AuRaRewardCalculator(AuRaParameters auRaParameters, IAbiEncoder abiEncoder)
        {
            _blockRewardCalculator = new StaticRewardCalculator(auRaParameters.BlockReward);
            _blockRewardContractTransition = auRaParameters.BlockRewardContractTransition;
            _blockRewardContractAddress = auRaParameters.BlockRewardContractAddress;
            _contract = new RewardContract(abiEncoder);
        }

        public BlockReward[] CalculateRewards(Block block, ITransactionProcessor transactionProcessor)
            => block.Number < _blockRewardContractTransition
                ? _blockRewardCalculator.CalculateRewards(block, transactionProcessor)
                : CalculateRewardsWithContract(block, transactionProcessor);

        private BlockReward[] CalculateRewardsWithContract(Block block, ITransactionProcessor transactionProcessor)
        {
            var transaction = _contract.Reward(_blockRewardContractAddress, block, new[] {block.Beneficiary}, new ushort[] {0});
            SystemContract.InvokeTransaction(block.Header, transactionProcessor, transaction, _tracer);
            var (addresses, rewards) = _contract.DecodeRewards(_tracer.ReturnValue);

            var blockRewards = new BlockReward[addresses.Length];
            for (int i = 0; i < addresses.Length; i++)
            {
                blockRewards[i] = new BlockReward(addresses[i], rewards[i]);
            }

            return blockRewards;
        }
    }
}