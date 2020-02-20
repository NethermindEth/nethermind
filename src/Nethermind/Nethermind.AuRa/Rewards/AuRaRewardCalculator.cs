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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Nethermind.Abi;
using Nethermind.AuRa.Contracts;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Rewards;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;

namespace Nethermind.AuRa.Rewards
{
    public class AuRaRewardCalculator : IRewardCalculator
    {
        private readonly ITransactionProcessor _transactionProcessor;
        private readonly StaticRewardCalculator _blockRewardCalculator;
        private readonly IList<RewardContract> _contracts;
        private readonly CallOutputTracer _tracer = new CallOutputTracer();

        public AuRaRewardCalculator(AuRaParameters auRaParameters, IAbiEncoder abiEncoder, ITransactionProcessor transactionProcessor)
        {
            IList<RewardContract> BuildTransitions()
            {
                var contracts = new List<RewardContract>();
                
                if (auRaParameters.BlockRewardContractTransition.HasValue)
                {
                    contracts.Add(new RewardContract(abiEncoder, auRaParameters.BlockRewardContractAddress, auRaParameters.BlockRewardContractTransition.Value));
                }

                if (auRaParameters.BlockRewardContractTransitions != null)
                {
                    contracts.AddRange(auRaParameters.BlockRewardContractTransitions.Select(t => new RewardContract(abiEncoder, t.Value, t.Key)));
                }

                contracts.Sort((a, b) => a.TransitionBlock.CompareTo(b.TransitionBlock));

                return contracts;
            }

            if (auRaParameters == null) throw new ArgumentNullException(nameof(AuRaParameters));
            _transactionProcessor = transactionProcessor ?? throw new ArgumentNullException(nameof(transactionProcessor));
            _contracts = BuildTransitions();
            _blockRewardCalculator = new StaticRewardCalculator(auRaParameters.BlockReward);
        }

        public BlockReward[] CalculateRewards(Block block)
            => TryGetContract(block.Number, out var contract)
                ? CalculateRewardsWithContract(block, contract)
                : _blockRewardCalculator.CalculateRewards(block);

        private bool TryGetContract(in long blockNumber, out RewardContract contract)
        {
            var index = _contracts.BinarySearch(blockNumber, (b, c) => c.TransitionBlock.CompareTo(b));
            if (index >= 0)
            {
                contract = _contracts[index];
                return true;
            }
            else
            {
                var largerIndex = ~index;
                if (largerIndex != 0)
                {
                    contract = _contracts[largerIndex - 1];
                    return true;
                }
                else
                {
                    contract = default;
                    return false;
                }
            }
        }

        private BlockReward[] CalculateRewardsWithContract(Block block, RewardContract contract)
        {
            (Address[] beneficieries, ushort[] kinds) GetBeneficiaries()
            {
                var length = block.Ommers.Length + 1;
                
                Address[] beneficiariesList = new Address[length];
                ushort[] kindsList = new ushort[length];
                beneficiariesList[0] = block.Beneficiary;
                kindsList[0] = RewardContract.Definition.BenefactorKind.Author;
                
                for (int i = 0; i < block.Ommers.Length; i++)
                {
                    var uncle = block.Ommers[i];
                    if (RewardContract.Definition.BenefactorKind.TryGetUncle(block.Number - uncle.Number, out var kind))
                    {
                        beneficiariesList[i + 1] = uncle.Beneficiary;
                        kindsList[i + 1] = kind;
                    }
                }

                return (beneficiariesList, kindsList);
            }

            var (beneficiaries, kinds) = GetBeneficiaries();
            var transaction = contract.Reward(beneficiaries, kinds);
            contract.InvokeTransaction(block.Header, _transactionProcessor, transaction, _tracer);
            var (addresses, rewards) = contract.DecodeRewards(_tracer.ReturnValue);

            var blockRewards = new BlockReward[addresses.Length];
            for (int index = 0; index < addresses.Length; index++)
            {
                var address = addresses[index];
                blockRewards[index] = new BlockReward(address, rewards[index], GetBlockRewardType(address, beneficiaries, kinds, index));
            }

            return blockRewards;
        }

        private BlockRewardType GetBlockRewardType(Address address, Address[] beneficiaries, ushort[] kinds, int index)
        {
            bool TryGetKind(int indexIn, ref ushort kindOut)
            {
                if (beneficiaries[indexIn] == address)
                {
                    kindOut = kinds[indexIn];
                    return true;
                }

                return false;
            }
            
            bool indexInBounds = index < beneficiaries.Length;
            ushort kind = RewardContract.Definition.BenefactorKind.External;
            if (!indexInBounds || !TryGetKind(index, ref kind))
            {
                for (int i = 0; i < beneficiaries.Length; i++)
                {
                    if (TryGetKind(i, ref kind))
                    {
                        break;
                    }
                }
            }

            return RewardContract.Definition.BenefactorKind.ToBlockRewardType(kind);
        }

        public static IRewardCalculatorSource GetSource(AuRaParameters auRaParameters, IAbiEncoder abiEncoder) => new AuRaRewardCalculatorSource(auRaParameters, abiEncoder);

        private class AuRaRewardCalculatorSource : IRewardCalculatorSource
        {
            private readonly AuRaParameters _auRaParameters;
            private readonly IAbiEncoder _abiEncoder;

            public AuRaRewardCalculatorSource(AuRaParameters auRaParameters, IAbiEncoder abiEncoder)
            {
                _auRaParameters = auRaParameters;
                _abiEncoder = abiEncoder;
            }

            public IRewardCalculator Get(ITransactionProcessor processor) => new AuRaRewardCalculator(_auRaParameters, _abiEncoder, processor);
        }
    }
}