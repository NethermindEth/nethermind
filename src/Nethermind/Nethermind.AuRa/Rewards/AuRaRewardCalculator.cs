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
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
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
        private readonly ITransactionProcessor _transactionProcessor;
        private readonly long _blockRewardContractTransition;
        private readonly StaticRewardCalculator _blockRewardCalculator;
        private readonly RewardContract _contract;
        private readonly CallOutputTracer _tracer = new CallOutputTracer();

        public AuRaRewardCalculator(AuRaParameters auRaParameters, IAbiEncoder abiEncoder,  ITransactionProcessor transactionProcessor)
        {
            if (auRaParameters == null) throw new ArgumentNullException(nameof(AuRaParameters));
            _transactionProcessor = transactionProcessor ?? throw new ArgumentNullException(nameof(transactionProcessor));
            _blockRewardContractTransition = auRaParameters.BlockRewardContractTransition;
            _contract = new RewardContract(abiEncoder, auRaParameters.BlockRewardContractAddress);
            _blockRewardCalculator = new StaticRewardCalculator(auRaParameters.BlockReward);
        }

        public BlockReward[] CalculateRewards(Block block)
            => block.Number < _blockRewardContractTransition
                ? _blockRewardCalculator.CalculateRewards(block)
                : CalculateRewardsWithContract(block);

        private BlockReward[] CalculateRewardsWithContract(Block block)
        {
            (Address[] beneficieries, ushort[] kinds) GetBeneficiaries()
            {
                var length = block.Ommers.Length + 1;
                if (length > 1)
                {
                    List<Address> beneficiariesList = new List<Address>(length) {block.Beneficiary};
                    List<ushort> kindsList = new List<ushort>(length) {RewardContract.Definition.BenefactorKind.Author};
                    
                    for (int i = 0; i < block.Ommers.Length; i++)
                    {
                        var uncle = block.Ommers[i];
                        if (RewardContract.Definition.BenefactorKind.TryGetUncle(block.Number - uncle.Number, out var kind))
                        {
                            beneficiariesList.Add(uncle.Beneficiary);
                            kindsList.Add(kind);
                        }
                    }

                    return (beneficiariesList.ToArray(), kindsList.ToArray());
                }

                return (new[] {block.Beneficiary}, new[] {RewardContract.Definition.BenefactorKind.Author});
            }

            var (beneficiaries, kinds) = GetBeneficiaries();
            var transaction = _contract.Reward(beneficiaries, kinds);
            _contract.InvokeTransaction(block.Header, _transactionProcessor, transaction, _tracer);
            var (addresses, rewards) = _contract.DecodeRewards(_tracer.ReturnValue);

            var blockRewards = new BlockReward[addresses.Length];
            for (int i = 0; i < addresses.Length; i++)
            {
                blockRewards[i] = new BlockReward(addresses[i], rewards[i], RewardContract.Definition.BenefactorKind.ToBlockRewardType(kinds[i]));
            }

            return blockRewards;
        }
    }
}