// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle.Json;

namespace Nethermind.Specs.ChainSpecStyle;

/// <summary>
/// This class can load a Parity-style chain spec file and build a <see cref="ChainSpec"/> out of it.
/// </summary>
public class ChainSpecLoader(IJsonSerializer serializer) : IChainSpecLoader
{
    public ChainSpec Load(Stream streamData)
    {
        try
        {
            ChainSpecJson chainSpecJson = serializer.Deserialize<ChainSpecJson>(streamData);
            return InitChainSpecFrom(chainSpecJson);
        }
        catch (Exception e)
        {
            throw new InvalidDataException($"Error when loading chainspec ({e.Message})", e);
        }
    }

    private ChainSpec InitChainSpecFrom(ChainSpecJson chainSpecJson)
    {
        ChainSpec chainSpec = new();

        chainSpec.NetworkId = chainSpecJson.Params.NetworkId ?? chainSpecJson.Params.ChainId ?? 1;
        chainSpec.ChainId = chainSpecJson.Params.ChainId ?? chainSpec.NetworkId;
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

    private void LoadParameters(ChainSpecJson chainSpecJson, ChainSpec chainSpec)
    {
        long? GetTransitions(string builtInName, Predicate<KeyValuePair<string, JsonElement>> predicate)
        {
            var allocation = chainSpecJson.Accounts?.Values.FirstOrDefault(v => v.BuiltIn?.Name.Equals(builtInName, StringComparison.InvariantCultureIgnoreCase) == true);
            if (allocation is null) return null;
            KeyValuePair<string, JsonElement>[] pricing = allocation.BuiltIn.Pricing.Where(o => predicate(o)).ToArray();
            if (pricing.Length > 0)
            {
                string key = pricing[0].Key;
                return long.TryParse(key, out long transition) ? transition : Convert.ToInt64(key, 16);
            }

            return null;
        }

        long? GetTransitionForExpectedPricing(string builtInName, string innerPath, long expectedValue)
        {
            bool GetForExpectedPricing(KeyValuePair<string, JsonElement> o) =>
                o.Value.TryGetSubProperty(innerPath, out JsonElement value) && value.GetInt64() == expectedValue;

            return GetTransitions(builtInName, GetForExpectedPricing);
        }

        long? GetTransitionIfInnerPathExists(string builtInName, string innerPath)
        {
            bool GetForInnerPathExistence(KeyValuePair<string, JsonElement> o) =>
                o.Value.TryGetSubProperty(innerPath, out _);

            return GetTransitions(builtInName, GetForInnerPathExistence);
        }

        ValidateParams(chainSpecJson.Params);

        chainSpec.Parameters = new ChainParameters
        {
            GasLimitBoundDivisor = chainSpecJson.Params.GasLimitBoundDivisor ?? 0x0400,
            MaximumExtraDataSize = chainSpecJson.Params.MaximumExtraDataSize ?? 32,
            MinGasLimit = chainSpecJson.Params.MinGasLimit ?? 5000,
            MaxCodeSize = chainSpecJson.Params.MaxCodeSize,
            MaxCodeSizeTransition = chainSpecJson.Params.MaxCodeSizeTransition,
            MaxCodeSizeTransitionTimestamp = chainSpecJson.Params.MaxCodeSizeTransitionTimestamp,
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
            Eip3607Transition = chainSpecJson.Params.Eip3607Transition,
            Eip1153TransitionTimestamp = chainSpecJson.Params.Eip1153TransitionTimestamp,
            Eip3651TransitionTimestamp = chainSpecJson.Params.Eip3651TransitionTimestamp,
            Eip3855TransitionTimestamp = chainSpecJson.Params.Eip3855TransitionTimestamp,
            Eip3860TransitionTimestamp = chainSpecJson.Params.Eip3860TransitionTimestamp,
            Eip4895TransitionTimestamp = chainSpecJson.Params.Eip4895TransitionTimestamp,
            Eip4844TransitionTimestamp = chainSpecJson.Params.Eip4844TransitionTimestamp,
            Eip2537TransitionTimestamp = chainSpecJson.Params.Eip2537TransitionTimestamp,
            Eip5656TransitionTimestamp = chainSpecJson.Params.Eip5656TransitionTimestamp,
            Eip6780TransitionTimestamp = chainSpecJson.Params.Eip6780TransitionTimestamp,
            Eip4788TransitionTimestamp = chainSpecJson.Params.Eip4788TransitionTimestamp,
            Eip4788ContractAddress = chainSpecJson.Params.Eip4788ContractAddress ?? Eip4788Constants.BeaconRootsAddress,
            Eip2935TransitionTimestamp = chainSpecJson.Params.Eip2935TransitionTimestamp,
            Eip2935ContractAddress = chainSpecJson.Params.Eip2935ContractAddress ?? Eip2935Constants.BlockHashHistoryAddress,
            TransactionPermissionContract = chainSpecJson.Params.TransactionPermissionContract,
            TransactionPermissionContractTransition = chainSpecJson.Params.TransactionPermissionContractTransition,
            ValidateChainIdTransition = chainSpecJson.Params.ValidateChainIdTransition,
            ValidateReceiptsTransition = chainSpecJson.Params.ValidateReceiptsTransition,
            Eip1559ElasticityMultiplier = chainSpecJson.Params.Eip1559ElasticityMultiplier ?? Eip1559Constants.DefaultElasticityMultiplier,
            Eip1559BaseFeeInitialValue = chainSpecJson.Params.Eip1559BaseFeeInitialValue ?? Eip1559Constants.DefaultForkBaseFee,
            Eip1559BaseFeeMaxChangeDenominator = chainSpecJson.Params.Eip1559BaseFeeMaxChangeDenominator ??
                                                 Eip1559Constants.DefaultBaseFeeMaxChangeDenominator,
            Eip1559FeeCollector = chainSpecJson.Params.Eip1559FeeCollector,
            Eip1559FeeCollectorTransition = chainSpecJson.Params.Eip1559FeeCollectorTransition,
            Eip1559BaseFeeMinValueTransition = chainSpecJson.Params.Eip1559BaseFeeMinValueTransition,
            Eip1559BaseFeeMinValue = chainSpecJson.Params.Eip1559BaseFeeMinValue,
            Eip4844BlobGasPriceUpdateFraction = chainSpecJson.Params.Eip4844BlobGasPriceUpdateFraction,
            Eip4844MaxBlobGasPerBlock = chainSpecJson.Params.Eip4844MaxBlobGasPerBlock,
            Eip4844MinBlobGasPrice = chainSpecJson.Params.Eip4844MinBlobGasPrice,
            Eip4844TargetBlobGasPerBlock = chainSpecJson.Params.Eip4844TargetBlobGasPerBlock,
            MergeForkIdTransition = chainSpecJson.Params.MergeForkIdTransition,
            TerminalTotalDifficulty = chainSpecJson.Params.TerminalTotalDifficulty,
            TerminalPoWBlockNumber = chainSpecJson.Params.TerminalPoWBlockNumber
        };

        chainSpec.Parameters.Eip152Transition ??= GetTransitionForExpectedPricing("blake2_f", "price.blake2_f.gas_per_round", 1);
        chainSpec.Parameters.Eip1108Transition ??= GetTransitionForExpectedPricing("alt_bn128_add", "price.alt_bn128_const_operations.price", 150)
                                                   ?? GetTransitionForExpectedPricing("alt_bn128_mul", "price.alt_bn128_const_operations.price", 6000)
                                                   ?? GetTransitionForExpectedPricing("alt_bn128_pairing", "price.alt_bn128_pairing.base", 45000);
        chainSpec.Parameters.Eip2565Transition ??= GetTransitionIfInnerPathExists("modexp", "price.modexp2565");

        Eip4844Constants.OverrideIfAny(
            chainSpec.Parameters.Eip4844BlobGasPriceUpdateFraction,
            chainSpec.Parameters.Eip4844MaxBlobGasPerBlock,
            chainSpec.Parameters.Eip4844MinBlobGasPrice,
            chainSpec.Parameters.Eip4844TargetBlobGasPerBlock);
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
        if (chainSpecJson.Engine?.Ethash is not null)
        {
            chainSpec.HomesteadBlockNumber = chainSpecJson.Engine.Ethash.HomesteadTransition;
            chainSpec.DaoForkBlockNumber = chainSpecJson.Engine.Ethash.DaoHardforkTransition;
        }
        else
        {
            chainSpec.HomesteadBlockNumber = 0;
        }

        IEnumerable<long?> difficultyBombDelaysBlockNumbers = chainSpec.Ethash?.DifficultyBombDelays
            .Keys
            .Cast<long?>()
            .ToArray();

        chainSpec.TangerineWhistleBlockNumber = chainSpec.Parameters.Eip150Transition;
        chainSpec.SpuriousDragonBlockNumber = chainSpec.Parameters.Eip160Transition;
        chainSpec.ByzantiumBlockNumber = chainSpec.Parameters.Eip140Transition;
        chainSpec.ConstantinopleBlockNumber =
            chainSpec.Parameters.Eip1283DisableTransition is null
                ? null
                : chainSpec.Parameters.Eip145Transition;
        chainSpec.ConstantinopleFixBlockNumber =
            chainSpec.Parameters.Eip1283DisableTransition ?? chainSpec.Parameters.Eip145Transition;
        chainSpec.IstanbulBlockNumber = chainSpec.Parameters.Eip2200Transition;
        chainSpec.MuirGlacierNumber = difficultyBombDelaysBlockNumbers?.Skip(2).FirstOrDefault();
        chainSpec.BerlinBlockNumber = chainSpec.Parameters.Eip2929Transition;
        chainSpec.LondonBlockNumber = chainSpec.Parameters.Eip1559Transition;
        chainSpec.ArrowGlacierBlockNumber = difficultyBombDelaysBlockNumbers?.Skip(4).FirstOrDefault();
        chainSpec.GrayGlacierBlockNumber = difficultyBombDelaysBlockNumbers?.Skip(5).FirstOrDefault();
        chainSpec.ShanghaiTimestamp = chainSpec.Parameters.Eip3651TransitionTimestamp;
        chainSpec.CancunTimestamp = chainSpec.Parameters.Eip4844TransitionTimestamp;

        // TheMerge parameters
        chainSpec.MergeForkIdBlockNumber = chainSpec.Parameters.MergeForkIdTransition;
        chainSpec.TerminalPoWBlockNumber = chainSpec.Parameters.TerminalPoWBlockNumber;
        chainSpec.TerminalTotalDifficulty = chainSpec.Parameters.TerminalTotalDifficulty;
    }

    private static void LoadEngine(ChainSpecJson chainSpecJson, ChainSpec chainSpec)
    {
        static AuRaParameters.Validator LoadValidator(ChainSpecJson.AuRaValidatorJson validatorJson, int level = 0)
        {
            AuRaParameters.ValidatorType validatorType = validatorJson.GetValidatorType();
            AuRaParameters.Validator validator = new() { ValidatorType = validatorType };
            switch (validator.ValidatorType)
            {
                case AuRaParameters.ValidatorType.List:
                    validator.Addresses = validatorJson.List;
                    break;
                case AuRaParameters.ValidatorType.Contract:
                    validator.Addresses = new[] { validatorJson.SafeContract };
                    break;
                case AuRaParameters.ValidatorType.ReportingContract:
                    validator.Addresses = new[] { validatorJson.Contract };
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

        if (chainSpecJson.Engine?.AuthorityRound is not null)
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
                RewriteBytecode = chainSpecJson.Engine.AuthorityRound.RewriteBytecode,
                WithdrawalContractAddress = chainSpecJson.Engine.AuthorityRound.WithdrawalContractAddress,
            };
        }
        else if (chainSpecJson.Engine?.Clique is not null)
        {
            chainSpec.SealEngineType = SealEngineType.Clique;
            chainSpec.Clique = new CliqueParameters
            {
                Epoch = chainSpecJson.Engine.Clique.Epoch,
                Period = chainSpecJson.Engine.Clique.Period,
                Reward = chainSpecJson.Engine.Clique.BlockReward ?? UInt256.Zero
            };
        }
        else if (chainSpecJson.Engine?.Ethash is not null)
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
            if (chainSpecJson.Engine.Ethash.DifficultyBombDelays is not null)
            {
                foreach (KeyValuePair<string, long> reward in chainSpecJson.Engine.Ethash.DifficultyBombDelays)
                {
                    long key = reward.Key.StartsWith("0x") ?
                        long.Parse(reward.Key.AsSpan(2), NumberStyles.HexNumber) :
                        long.Parse(reward.Key);

                    chainSpec.Ethash.DifficultyBombDelays.Add(key, reward.Value);
                }
            }
        }
        else if (chainSpecJson.Engine?.Optimism is not null)
        {
            chainSpec.SealEngineType = SealEngineType.Optimism;
            chainSpec.Optimism = new OptimismParameters
            {
                RegolithTimestamp = chainSpecJson.Engine.Optimism.RegolithTimestamp,
                BedrockBlockNumber = chainSpecJson.Engine.Optimism.BedrockBlockNumber,
                CanyonTimestamp = chainSpecJson.Engine.Optimism.CanyonTimestamp,
                EcotoneTimestamp = chainSpecJson.Engine.Optimism.EcotoneTimestamp,
                FjordTimestamp = chainSpecJson.Engine.Optimism.FjordTimestamp,

                L1FeeRecipient = chainSpecJson.Engine.Optimism.L1FeeRecipient,
                L1BlockAddress = chainSpecJson.Engine.Optimism.L1BlockAddress,
                CanyonBaseFeeChangeDenominator = chainSpecJson.Engine.Optimism.CanyonBaseFeeChangeDenominator,
                Create2DeployerAddress = chainSpecJson.Engine.Optimism.Create2DeployerAddress,
                Create2DeployerCode = chainSpecJson.Engine.Optimism.Create2DeployerCode
            };
        }
        else if (chainSpecJson.Engine?.NethDev is not null)
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
        if (chainSpecJson.Genesis is null)
        {
            return;
        }

        UInt256 nonce = chainSpecJson.Genesis.Seal?.Ethereum?.Nonce ?? 0;
        Hash256 mixHash = chainSpecJson.Genesis.Seal?.Ethereum?.MixHash ?? Keccak.Zero;

        byte[] auRaSignature = chainSpecJson.Genesis.Seal?.AuthorityRound?.Signature;
        long? step = chainSpecJson.Genesis.Seal?.AuthorityRound?.Step;

        Hash256 parentHash = chainSpecJson.Genesis.ParentHash ?? Keccak.Zero;
        ulong timestamp = chainSpecJson.Genesis.Timestamp;
        UInt256 difficulty = chainSpecJson.Genesis.Difficulty;
        byte[] extraData = chainSpecJson.Genesis.ExtraData ?? Array.Empty<byte>();
        UInt256 gasLimit = chainSpecJson.Genesis.GasLimit;
        Address beneficiary = chainSpecJson.Genesis.Author ?? Address.Zero;
        UInt256 baseFee = chainSpecJson.Genesis.BaseFeePerGas ?? UInt256.Zero;
        if (chainSpecJson.Params.Eip1559Transition is not null)
            baseFee = chainSpecJson.Params.Eip1559Transition == 0
                ? (chainSpecJson.Genesis.BaseFeePerGas ?? Eip1559Constants.DefaultForkBaseFee)
                : UInt256.Zero;

        Hash256 stateRoot = chainSpecJson.Genesis.StateRoot ?? Keccak.EmptyTreeHash;
        chainSpec.GenesisStateUnavailable = chainSpecJson.Genesis.StateUnavailable;

        BlockHeader genesisHeader = new(
            parentHash,
            Keccak.OfAnEmptySequenceRlp,
            beneficiary,
            difficulty,
            0,
            (long)gasLimit,
            timestamp,
            extraData);

        genesisHeader.Author = beneficiary;
        genesisHeader.Hash = Keccak.Zero; // need to run the block to know the actual hash
        genesisHeader.Bloom = Bloom.Empty;
        genesisHeader.MixHash = mixHash;
        genesisHeader.Nonce = (ulong)nonce;
        genesisHeader.ReceiptsRoot = Keccak.EmptyTreeHash;
        genesisHeader.StateRoot = stateRoot;
        genesisHeader.TxRoot = Keccak.EmptyTreeHash;
        genesisHeader.BaseFeePerGas = baseFee;
        bool withdrawalsEnabled = chainSpecJson.Params.Eip4895TransitionTimestamp is not null && genesisHeader.Timestamp >= chainSpecJson.Params.Eip4895TransitionTimestamp;
        if (withdrawalsEnabled)
            genesisHeader.WithdrawalsRoot = Keccak.EmptyTreeHash;

        bool isEip4844Enabled = chainSpecJson.Params.Eip4844TransitionTimestamp is not null && genesisHeader.Timestamp >= chainSpecJson.Params.Eip4844TransitionTimestamp;
        if (isEip4844Enabled)
        {
            genesisHeader.BlobGasUsed = chainSpecJson.Genesis.BlobGasUsed;
            genesisHeader.ExcessBlobGas = chainSpecJson.Genesis.ExcessBlobGas;
        }

        bool isEip4788Enabled = chainSpecJson.Params.Eip4788TransitionTimestamp is not null && genesisHeader.Timestamp >= chainSpecJson.Params.Eip4788TransitionTimestamp;
        if (isEip4788Enabled)
        {
            genesisHeader.ParentBeaconBlockRoot = Keccak.Zero;
        }

        genesisHeader.AuRaStep = step;
        genesisHeader.AuRaSignature = auRaSignature;

        if (withdrawalsEnabled)
            chainSpec.Genesis = new Block(genesisHeader, Array.Empty<Transaction>(), Array.Empty<BlockHeader>(), Array.Empty<Withdrawal>());
        else
            chainSpec.Genesis = new Block(genesisHeader);
    }

    private static void LoadAllocations(ChainSpecJson chainSpecJson, ChainSpec chainSpec)
    {
        if (chainSpecJson.Accounts is null)
        {
            return;
        }

        chainSpec.Allocations = new Dictionary<Address, ChainSpecAllocation>();
        foreach (KeyValuePair<string, AllocationJson> account in chainSpecJson.Accounts)
        {
            if (account.Value.BuiltIn is not null && account.Value.Balance is null)
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
        if (chainSpecJson.Nodes is null)
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
