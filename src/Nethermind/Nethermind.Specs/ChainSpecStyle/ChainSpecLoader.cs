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

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle.Json;
using Newtonsoft.Json.Linq;

namespace Nethermind.Specs.ChainSpecStyle
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
            _serializer.RegisterConverter(new StepDurationJsonConverter());
            _serializer.RegisterConverter(new BlockRewardJsonConverter());
        }

        public ChainSpec Load(byte[] data) => Load(Encoding.UTF8.GetString(data));

        public ChainSpec Load(string jsonData)
        {
            try
            {
                ChainSpecJson chainSpecJson = _serializer.Deserialize<ChainSpecJson>(jsonData);
                ChainSpec chainSpec = new();

                chainSpec.ChainId = chainSpecJson.Params.NetworkId;
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
            long? GetTransitions(string builtInName, Predicate<KeyValuePair<string, JObject>> predicate)
            {
                var allocation = chainSpecJson.Accounts.Values.FirstOrDefault(v => v.BuiltIn?.Name.Equals(builtInName, StringComparison.InvariantCultureIgnoreCase) == true);
                if (allocation == null) return null;
                KeyValuePair<string, JObject>[] pricing = allocation.BuiltIn.Pricing.Where(o => predicate(o)).ToArray();
                if (pricing.Length > 0)
                {
                    string key = pricing[0].Key;
                    return long.TryParse(key, out long transition) ? transition : Convert.ToInt64(key, 16);
                }

                return null;
            }
            
            long? GetTransitionForExpectedPricing(string builtInName, string innerPath, long expectedValue)
            {
                bool GetForExpectedPricing(KeyValuePair<string, JObject> o) => o.Value.SelectToken(innerPath)?.Value<long>() == expectedValue;
                return GetTransitions(builtInName, GetForExpectedPricing);
            }
            
            long? GetTransitionIfInnerPathExists(string builtInName, string innerPath)
            {
                bool GetForInnerPathExistence(KeyValuePair<string, JObject> o) => o.Value.SelectToken(innerPath) != null;
                return GetTransitions(builtInName, GetForInnerPathExistence);
            }

            ValidateParams(chainSpecJson.Params);

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
                Eip7Transition = chainSpecJson.Params.Eip7Transition,
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
                Eip1283ReenableTransition = chainSpecJson.Params.Eip1283ReenableTransition,
                Eip1344Transition = chainSpecJson.Params.Eip1344Transition,
                Eip1706Transition = chainSpecJson.Params.Eip1706Transition,
                Eip1884Transition = chainSpecJson.Params.Eip1884Transition,
                Eip2028Transition = chainSpecJson.Params.Eip2028Transition,
                Eip2200Transition = chainSpecJson.Params.Eip2200Transition,
                Eip1559Transition = chainSpecJson.Params.Eip1559Transition,
                Eip2315Transition = chainSpecJson.Params.Eip2315Transition,
                Eip2537Transition = chainSpecJson.Params.Eip2537Transition,
                Eip2565Transition = chainSpecJson.Params.Eip2565Transition,
                Eip2929Transition = chainSpecJson.Params.Eip2929Transition,
                Eip2930Transition = chainSpecJson.Params.Eip2930Transition,
                Eip3198Transition = chainSpecJson.Params.Eip3198Transition,
                Eip3541Transition = chainSpecJson.Params.Eip3541Transition,
                Eip3529Transition = chainSpecJson.Params.Eip3529Transition,
                TransactionPermissionContract = chainSpecJson.Params.TransactionPermissionContract,
                TransactionPermissionContractTransition = chainSpecJson.Params.TransactionPermissionContractTransition,
                ValidateChainIdTransition = chainSpecJson.Params.ValidateChainIdTransition,
                ValidateReceiptsTransition = chainSpecJson.Params.ValidateReceiptsTransition,
                Eip1559ElasticityMultiplier = chainSpecJson.Params.Eip1559ElasticityMultiplier ?? Eip1559Constants.ElasticityMultiplier,
                Eip1559BaseFeeInitialValue = chainSpecJson.Params.Eip1559BaseFeeInitialValue ?? Eip1559Constants.ForkBaseFee,
                Eip1559BaseFeeMaxChangeDenominator = chainSpecJson.Params.Eip1559BaseFeeMaxChangeDenominator ??
                                                     Eip1559Constants.BaseFeeMaxChangeDenominator
            };

            chainSpec.Parameters.Eip152Transition ??= GetTransitionForExpectedPricing("blake2_f", "price.blake2_f.gas_per_round", 1);
            chainSpec.Parameters.Eip1108Transition ??= GetTransitionForExpectedPricing("alt_bn128_add", "price.alt_bn128_const_operations.price", 150)
                                                       ?? GetTransitionForExpectedPricing("alt_bn128_mul", "price.alt_bn128_const_operations.price", 6000)
                                                       ?? GetTransitionForExpectedPricing("alt_bn128_pairing", "price.alt_bn128_pairing.base", 45000);
            chainSpec.Parameters.Eip2565Transition ??= GetTransitionIfInnerPathExists("modexp", "price.modexp2565");

            Eip1559Constants.ElasticityMultiplier = chainSpec.Parameters.Eip1559ElasticityMultiplier;
            Eip1559Constants.ForkBaseFee = chainSpec.Parameters.Eip1559BaseFeeInitialValue;
            Eip1559Constants.BaseFeeMaxChangeDenominator = chainSpec.Parameters.Eip1559BaseFeeMaxChangeDenominator;
        }

        private static void ValidateParams(ChainSpecParamsJson parameters)
        {
            if (parameters.Eip1283ReenableTransition != parameters.Eip1706Transition
                && parameters.Eip1283DisableTransition.HasValue)
            {
                throw new InvalidOperationException("When 'Eip1283ReenableTransition' or 'Eip1706Transition' are provided they have to have same value as they are both part of 'Eip2200Transition'.");
            }

            if (parameters.Eip1706Transition.HasValue
                && parameters.Eip2200Transition.HasValue)
            {
                throw new InvalidOperationException("Both 'Eip2200Transition' and 'Eip1706Transition' are provided. Please provide either 'Eip2200Transition' or pair of 'Eip1283ReenableTransition' and 'Eip1706Transition' as they have same meaning.");
            }
        }

        private static void LoadTransitions(ChainSpecJson chainSpecJson, ChainSpec chainSpec)
        {
            if (chainSpecJson.Engine?.Ethash != null)
            {
                chainSpec.HomesteadBlockNumber = chainSpecJson.Engine.Ethash.HomesteadTransition;
                chainSpec.DaoForkBlockNumber = chainSpecJson.Engine.Ethash.DaoHardforkTransition;
            }
            else
            {
                chainSpec.HomesteadBlockNumber = 0;
            }

            chainSpec.TangerineWhistleBlockNumber = chainSpec.Parameters.Eip150Transition;
            chainSpec.SpuriousDragonBlockNumber = chainSpec.Parameters.Eip160Transition;
            chainSpec.ByzantiumBlockNumber = chainSpec.Parameters.Eip140Transition;
            chainSpec.ConstantinopleBlockNumber =
                chainSpec.Parameters.Eip1283DisableTransition == null
                    ? null
                    : chainSpec.Parameters.Eip145Transition;
            chainSpec.ConstantinopleFixBlockNumber =
                chainSpec.Parameters.Eip1283DisableTransition ?? chainSpec.Parameters.Eip145Transition;
            chainSpec.IstanbulBlockNumber = chainSpec.Parameters.Eip2200Transition;
            chainSpec.MuirGlacierNumber = chainSpec.Ethash?.DifficultyBombDelays.Count > 2 ?
                chainSpec.Ethash?.DifficultyBombDelays.Keys.ToArray()[2]
                : null;
            chainSpec.BerlinBlockNumber = chainSpec.Parameters.Eip2929Transition ?? (long.MaxValue - 1);
            chainSpec.LondonBlockNumber = chainSpec.Parameters.Eip1559Transition ?? (long.MaxValue - 1);
        }

        private static void LoadEngine(ChainSpecJson chainSpecJson, ChainSpec chainSpec)
        {
            static AuRaParameters.Validator LoadValidator(ChainSpecJson.AuRaValidatorJson validatorJson, int level = 0)
            {
                AuRaParameters.ValidatorType validatorType = validatorJson.GetValidatorType();
                AuRaParameters.Validator validator = new() {ValidatorType = validatorType};
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
                    BlockRewardContractTransitions = chainSpecJson.Engine.AuthorityRound.BlockRewardContractTransitions,
                    ValidateScoreTransition = chainSpecJson.Engine.AuthorityRound.ValidateScoreTransition,
                    ValidateStepTransition = chainSpecJson.Engine.AuthorityRound.ValidateStepTransition,
                    Validators = LoadValidator(chainSpecJson.Engine.AuthorityRound.Validator),
                    RandomnessContractAddress = chainSpecJson.Engine.AuthorityRound.RandomnessContractAddress,
                    BlockGasLimitContractTransitions = chainSpecJson.Engine.AuthorityRound.BlockGasLimitContractTransitions,
                    TwoThirdsMajorityTransition = chainSpecJson.Engine.AuthorityRound.TwoThirdsMajorityTransition ?? AuRaParameters.TransitionDisabled,
                    PosdaoTransition = chainSpecJson.Engine.AuthorityRound.PosdaoTransition ?? AuRaParameters.TransitionDisabled,
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
                    DaoHardforkAccounts = chainSpecJson.Engine.Ethash.DaoHardforkAccounts ?? Array.Empty<Address>(),
                    Eip100bTransition = chainSpecJson.Engine.Ethash.Eip100bTransition ?? 0L,
                    FixedDifficulty = chainSpecJson.Engine.Ethash.FixedDifficulty,
                    BlockRewards = chainSpecJson.Engine.Ethash.BlockReward
                };

                chainSpec.Ethash.DifficultyBombDelays = new Dictionary<long, long>();
                if (chainSpecJson.Engine.Ethash.DifficultyBombDelays != null)
                {
                    foreach (KeyValuePair<string, long> reward in chainSpecJson.Engine.Ethash.DifficultyBombDelays)
                    {
                        chainSpec.Ethash.DifficultyBombDelays.Add(LongConverter.FromString(reward.Key), reward.Value);
                    }
                }
            }
            else if (chainSpecJson.Engine?.NethDev != null)
            {
                chainSpec.SealEngineType = SealEngineType.NethDev;
            }

            var customEngineType = chainSpecJson.Engine?.CustomEngineData?.FirstOrDefault().Key;
            
            if (!string.IsNullOrEmpty(customEngineType))
            {
                chainSpec.SealEngineType = customEngineType;
            }
            
            if (string.IsNullOrEmpty(chainSpec.SealEngineType))
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

            UInt256 nonce = chainSpecJson.Genesis.Seal.Ethereum?.Nonce ?? 0;
            Keccak mixHash = chainSpecJson.Genesis.Seal.Ethereum?.MixHash ?? Keccak.Zero;

            byte[] auRaSignature = chainSpecJson.Genesis.Seal.AuthorityRound?.Signature;
            long? step = chainSpecJson.Genesis.Seal.AuthorityRound?.Step;

            Keccak parentHash = chainSpecJson.Genesis.ParentHash ?? Keccak.Zero;
            UInt256 timestamp = chainSpecJson.Genesis.Timestamp;
            UInt256 difficulty = chainSpecJson.Genesis.Difficulty;
            byte[] extraData = chainSpecJson.Genesis.ExtraData ?? Array.Empty<byte>();
            UInt256 gasLimit = chainSpecJson.Genesis.GasLimit;
            Address beneficiary = chainSpecJson.Genesis.Author ?? Address.Zero;
            UInt256 baseFee = chainSpecJson.Genesis.BaseFeePerGas ?? UInt256.Zero;
            if (chainSpecJson.Params.Eip1559Transition != null)
                baseFee = chainSpecJson.Params.Eip1559Transition == 0
                    ? (chainSpecJson.Genesis.BaseFeePerGas ?? Eip1559Constants.DefaultForkBaseFee)
                    : UInt256.Zero;

            
            BlockHeader genesisHeader = new(
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
            genesisHeader.Bloom = Bloom.Empty;
            genesisHeader.MixHash = mixHash;
            genesisHeader.Nonce = (ulong) nonce;
            genesisHeader.ReceiptsRoot = Keccak.EmptyTreeHash;
            genesisHeader.StateRoot = Keccak.EmptyTreeHash;
            genesisHeader.TxRoot = Keccak.EmptyTreeHash;
            genesisHeader.BaseFeePerGas = baseFee;
            
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
                if (account.Value.BuiltIn != null && account.Value.Balance == null)
                {
                    continue;
                }
                
                chainSpec.Allocations[new Address(account.Key)] = new ChainSpecAllocation(
                    account.Value.Balance ?? UInt256.Zero,
                    account.Value.Nonce,
                    account.Value.Code,
                    account.Value.Constructor,
                    account.Value.GetConvertedStorage());
            }
        }

        private static void LoadBootnodes(ChainSpecJson chainSpecJson, ChainSpec chainSpec)
        {
            if (chainSpecJson.Nodes == null)
            {
                chainSpec.Bootnodes = Array.Empty<NetworkNode>();
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
