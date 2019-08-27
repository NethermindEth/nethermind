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
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Json;
using Nethermind.Core.Specs.ChainSpecStyle.Json;
using Nethermind.Core.Specs.GenesisFileStyle.Json;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Core.Specs.ChainSpecStyle
{
    /// <summary>
    /// This class can load a Parity-style chain spec file and build a <see cref="ChainSpec"/> out of it. 
    /// </summary>
    public class ChainSpecLoader : IChainSpecLoader
    {
        private readonly IJsonSerializer _serializer;

        public ChainSpecLoader(IJsonSerializer serializer)
        {
            _serializer = serializer;
        }

        public ChainSpec Load(byte[] data)
        {
            try
            {
                string jsonData = System.Text.Encoding.UTF8.GetString(data);
                var chainSpecJson = _serializer.Deserialize<ChainSpecJson>(jsonData);
                var chainSpec = new ChainSpec();

                chainSpec.ChainId = (int) chainSpecJson.Params.NetworkId;
                chainSpec.Name = chainSpecJson.Name;
                chainSpec.DataDir = chainSpecJson.DataDir;
                LoadGenesis(chainSpecJson, chainSpec);
                LoadEngine(chainSpecJson, chainSpec);
                LoadAllocations(chainSpecJson, chainSpec);
                LoadBootnodes(chainSpecJson, chainSpec);
                LoadParameters(chainSpecJson, chainSpec);
                LoadTransitions(chainSpecJson, chainSpec);

                return chainSpec;
            }
            catch (Exception e)
            {
                throw new InvalidDataException($"Error when loading chainspec ({e.Message})", e);
            }
        }

        private void LoadParameters(ChainSpecJson chainSpecJson, ChainSpec chainSpec)
        {
            chainSpec.Parameters = new ChainParameters
            {
                AccountStartNonce = chainSpecJson.Params.AccountStartNonce ?? UInt256.Zero,
                GasLimitBoundDivisor = chainSpecJson.Params.GasLimitBoundDivisor ?? 0x0400,
                MaximumExtraDataSize = chainSpecJson.Params.MaximumExtraDataSize ?? 32,
                MinGasLimit = chainSpecJson.Params.MinGasLimit ?? 5000,
                MaxCodeSize = chainSpecJson.Params.MaxCodeSize ?? long.MaxValue,
                MaxCodeSizeTransition = chainSpecJson.Params.MaxCodeSizeTransition ?? 0,
                Registrar = chainSpecJson.Params.EnsRegistrar,
                ForkBlock = chainSpecJson.Params.ForkBlock,
                ForkCanonHash = chainSpecJson.Params.ForkCanonHash,
                Eip150Transition = chainSpecJson.Params.Eip150Transition ?? 0,
                Eip152Transition = chainSpecJson.Params.Eip152Transition,
                Eip160Transition = chainSpecJson.Params.Eip160Transition ?? 0,
                Eip161abcTransition = chainSpecJson.Params.Eip161abcTransition ?? 0,
                Eip161dTransition = chainSpecJson.Params.Eip161dTransition ?? 0,
                Eip155Transition = chainSpecJson.Params.Eip155Transition ?? 0,
                Eip140Transition = chainSpecJson.Params.Eip140Transition,
                Eip211Transition = chainSpecJson.Params.Eip211Transition,
                Eip214Transition = chainSpecJson.Params.Eip214Transition,
                Eip658Transition = chainSpecJson.Params.Eip658Transition,
                Eip145Transition = chainSpecJson.Params.Eip145Transition,
                Eip1014Transition = chainSpecJson.Params.Eip1014Transition,
                Eip1052Transition = chainSpecJson.Params.Eip1052Transition,
                Eip1108Transition = chainSpecJson.Params.Eip1108Transition,
                Eip1283Transition = chainSpecJson.Params.Eip1283Transition,
                Eip1283DisableTransition = chainSpecJson.Params.Eip1283DisableTransition,
                Eip1344Transition = chainSpecJson.Params.Eip1344Transition,
                Eip1884Transition = chainSpecJson.Params.Eip1884Transition,
                Eip2028Transition = chainSpecJson.Params.Eip2028Transition,
                Eip2200Transition = chainSpecJson.Params.Eip2200Transition,
            };
        }

        private void LoadTransitions(ChainSpecJson chainSpecJson, ChainSpec chainSpec)
        {
            if (chainSpecJson.Engine?.Ethash != null)
            {
                chainSpec.HomesteadBlockNumber = chainSpecJson.Engine.Ethash.HomesteadTransition;
                chainSpec.DaoForkBlockNumber = chainSpecJson.Engine.Ethash.DaoHardForkTransition;
            }

            chainSpec.TangerineWhistleBlockNumber = chainSpec.Parameters.Eip150Transition;
            chainSpec.SpuriousDragonBlockNumber = chainSpec.Parameters.Eip160Transition;
            chainSpec.ByzantiumBlockNumber = chainSpec.Parameters.Eip140Transition;
            chainSpec.ConstantinopleBlockNumber = chainSpec.Parameters.Eip145Transition;
        }

        private void LoadEngine(ChainSpecJson chainSpecJson, ChainSpec chainSpec)
        {
            AuRaParameters.Validator LoadValidator(ChainSpecJson.AuRaValidatorJson validatorJson, int level = 0)
            {
                var validatorType = validatorJson.GetValidatorType();
                var validator = new AuRaParameters.Validator() {ValidatorType = validatorType};
                switch (validator.ValidatorType)
                {
                    case AuRaParameters.ValidatorType.List:
                        validator.Addresses = validatorJson.List;
                        break;
                    case AuRaParameters.ValidatorType.Contract:
                        validator.Addresses = new[] {validatorJson.SafeContract};
                        break;
                    case AuRaParameters.ValidatorType.ReportingContract:
                        validator.Addresses = new[] {validatorJson.Contract};
                        break;
                    case AuRaParameters.ValidatorType.Multi:
                        if (level != 0) throw new ArgumentException("AuRa multi validator cannot be inner validator.");
                        validator.Validators = validatorJson.Multi
                            .ToDictionary(kvp => kvp.Key, kvp => LoadValidator(kvp.Value, level + 1))
                            .ToImmutableSortedDictionary();
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                return validator;
            }

            if (chainSpecJson.Engine?.AuthorityRound != null)
            {
                chainSpec.SealEngineType = SealEngineType.AuRa;
                chainSpec.AuRa = new AuRaParameters
                {
                    MaximumUncleCount = chainSpecJson.Engine.AuthorityRound.MaximumUncleCount,
                    MaximumUncleCountTransition = chainSpecJson.Engine.AuthorityRound.MaximumUncleCountTransition,
                    StepDuration = chainSpecJson.Engine.AuthorityRound.StepDuration,
                    BlockReward = chainSpecJson.Engine.AuthorityRound.BlockReward,
                    BlockRewardContractAddress = chainSpecJson.Engine.AuthorityRound.BlockRewardContractAddress,
                    BlockRewardContractTransition = chainSpecJson.Engine.AuthorityRound.BlockRewardContractTransition,
                    Validators = LoadValidator(chainSpecJson.Engine.AuthorityRound.Validator)
                };
            }
            else if (chainSpecJson.Engine?.Clique != null)
            {
                chainSpec.SealEngineType = SealEngineType.Clique;
                chainSpec.Clique = new CliqueParameters
                {
                    Epoch = chainSpecJson.Engine.Clique.Epoch,
                    Period = chainSpecJson.Engine.Clique.Period,
                    Reward = chainSpecJson.Engine.Clique.BlockReward ?? UInt256.Zero
                };
            }
            else if (chainSpecJson.Engine?.Ethash != null)
            {
                chainSpec.SealEngineType = SealEngineType.Ethash;
                chainSpec.Ethash = new EthashParameters
                {
                    MinimumDifficulty = chainSpecJson.Engine.Ethash.MinimumDifficulty ?? 0L,
                    DifficultyBoundDivisor = chainSpecJson.Engine.Ethash.DifficultyBoundDivisor ?? 0x0800L,
                    DurationLimit = chainSpecJson.Engine.Ethash.DurationLimit ?? 13L,
                    HomesteadTransition = chainSpecJson.Engine.Ethash.HomesteadTransition ?? 0,
                    DaoHardforkTransition = chainSpecJson.Engine.Ethash.DaoHardforkTransition,
                    DaoHardforkBeneficiary = chainSpecJson.Engine.Ethash.DaoHardforkBeneficiary,
                    DaoHardforkAccounts = chainSpecJson.Engine.Ethash.DaoHardforkAccounts ?? new Address[0],
                    Eip100bTransition = chainSpecJson.Engine.Ethash.Eip100bTransition ?? 0L,
                    BlockRewards = new Dictionary<long, UInt256>()
                };

                foreach (KeyValuePair<string, UInt256> reward in chainSpecJson.Engine.Ethash.BlockReward)
                {
                    chainSpec.Ethash.BlockRewards.Add(LongConverter.FromString(reward.Key), reward.Value);
                }

                chainSpec.Ethash.DifficultyBombDelays = new Dictionary<long, long>();
                foreach (KeyValuePair<string, long> reward in chainSpecJson.Engine.Ethash.DifficultyBombDelays)
                {
                    chainSpec.Ethash.DifficultyBombDelays.Add(LongConverter.FromString(reward.Key), reward.Value);
                }
            }
            else if (chainSpecJson.Engine?.NethDev != null)
            {
                chainSpec.SealEngineType = SealEngineType.NethDev;
            }
            else
            {
                throw new NotSupportedException("unknown seal engine in chainspec");
            }
        }

        private static void LoadGenesis(ChainSpecJson chainSpecJson, ChainSpec chainSpec)
        {
            if (chainSpecJson.Genesis == null)
            {
                return;
            }

            var nonce = chainSpecJson.Genesis.Seal.Ethereum?.Nonce ?? 0;
            var mixHash = chainSpecJson.Genesis.Seal.Ethereum?.MixHash ?? Keccak.Zero;

            var auRaSignature = chainSpecJson.Genesis.Seal.AuthorityRound?.Signature;
            var step = chainSpecJson.Genesis.Seal.AuthorityRound?.Step;

            var parentHash = chainSpecJson.Genesis.ParentHash ?? Keccak.Zero;
            var timestamp = chainSpecJson.Genesis.Timestamp;
            var difficulty = chainSpecJson.Genesis.Difficulty;
            var extraData = chainSpecJson.Genesis.ExtraData ?? new byte[0];
            var gasLimit = chainSpecJson.Genesis.GasLimit;
            var beneficiary = chainSpecJson.Genesis.Author ?? Address.Zero;

            BlockHeader genesisHeader = new BlockHeader(
                parentHash,
                Keccak.OfAnEmptySequenceRlp,
                beneficiary,
                difficulty,
                0,
                (long) gasLimit,
                timestamp,
                extraData);

            genesisHeader.Author = beneficiary;
            genesisHeader.Hash = Keccak.Zero; // need to run the block to know the actual hash
            genesisHeader.Bloom = new Bloom();
            genesisHeader.GasUsed = 0;
            genesisHeader.MixHash = mixHash;
            genesisHeader.Nonce = (ulong) nonce;
            genesisHeader.ReceiptsRoot = Keccak.EmptyTreeHash;
            genesisHeader.StateRoot = Keccak.EmptyTreeHash;
            genesisHeader.TxRoot = Keccak.EmptyTreeHash;
            
            genesisHeader.AuRaStep = step;
            genesisHeader.AuRaSignature = auRaSignature;

            chainSpec.Genesis = new Block(genesisHeader);
        }

        private static void LoadAllocations(ChainSpecJson chainSpecJson, ChainSpec chainSpec)
        {
            if (chainSpecJson.Accounts == null)
            {
                return;
            }

            chainSpec.Allocations = new Dictionary<Address, ChainSpecAllocation>();
            foreach (KeyValuePair<string, AllocationJson> account in chainSpecJson.Accounts)
            {
                if (account.Value.BuiltIn != null && account.Value.Balance == UInt256.Zero)
                {
                    continue;
                }

                chainSpec.Allocations[new Address(account.Key)] = new ChainSpecAllocation(
                    account.Value.Balance,
                    account.Value.Code,
                    account.Value.Constructor);
            }
        }

        private static void LoadBootnodes(ChainSpecJson chainSpecJson, ChainSpec chainSpec)
        {
            if (chainSpecJson.Nodes == null)
            {
                chainSpec.Bootnodes = new NetworkNode[0];
                return;
            }

            chainSpec.Bootnodes = new NetworkNode[chainSpecJson.Nodes.Length];
            for (int i = 0; i < chainSpecJson.Nodes.Length; i++)
            {
                chainSpec.Bootnodes[i] = new NetworkNode(chainSpecJson.Nodes[i]);
            }
        }
    }
}