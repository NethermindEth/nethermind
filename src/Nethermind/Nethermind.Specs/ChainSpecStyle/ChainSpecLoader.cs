// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle.Json;

namespace Nethermind.Specs.ChainSpecStyle;

/// <summary>
/// This class can load a Parity-style chain spec file and build a <see cref="ChainSpec"/> out of it.
/// </summary>
public class ChainSpecLoader(IJsonSerializer serializer, ILogManager logManager) : IChainSpecLoader
{
    private readonly ILogger _logger = logManager.GetClassLogger<ChainSpecLoader>();

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
        ulong networkId = chainSpecJson.Params.NetworkId ?? chainSpecJson.Params.ChainId ?? 1;
        ChainSpec chainSpec = new()
        {
            NetworkId = networkId,
            ChainId = chainSpecJson.Params.ChainId ?? networkId,
            Name = chainSpecJson.Name,
            DataDir = chainSpecJson.DataDir
        };

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
            AllocationJson? allocation = chainSpecJson.Accounts?.Values.FirstOrDefault(v => v.BuiltIn?.Name.Equals(builtInName, StringComparison.OrdinalIgnoreCase) == true);
            if (allocation is null) return null;
            KeyValuePair<string, JsonElement>[] pricing = allocation.BuiltIn?.Pricing.Where(o => predicate(o)).ToArray();
            if (pricing?.Length > 0)
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

        ExpandHardforkLabels(chainSpecJson.Params);
        ValidateParams(chainSpecJson.Params);

        chainSpec.Parameters = new ChainParameters
        {
            GasLimitBoundDivisor = chainSpecJson.Params.GasLimitBoundDivisor ?? 0x0400,
            MaximumExtraDataSize = chainSpecJson.Params.MaximumExtraDataSize ?? 32,
            MinGasLimit = chainSpecJson.Params.MinGasLimit ?? 5000,
            MinHistoryRetentionEpochs = chainSpecJson.Params.MinHistoryRetentionEpochs ?? 82125,
            MaxCodeSize = chainSpecJson.Params.MaxCodeSize,
            MaxCodeSizeTransition = chainSpecJson.Params.MaxCodeSizeTransition,
            MaxCodeSizeTransitionTimestamp = chainSpecJson.Params.MaxCodeSizeTransitionTimestamp,
            Registrar = chainSpecJson.Params.Registrar,
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
            BeaconChainGenesisTimestamp = chainSpecJson.Params.BeaconChainGenesisTimestamp,
            Eip1153Transition = chainSpecJson.Params.Eip1153Transition,
            Eip1153TransitionTimestamp = chainSpecJson.Params.Eip1153TransitionTimestamp,
            Eip3651Transition = chainSpecJson.Params.Eip3651Transition,
            Eip3651TransitionTimestamp = chainSpecJson.Params.Eip3651TransitionTimestamp,
            Eip3855Transition = chainSpecJson.Params.Eip3855Transition,
            Eip3855TransitionTimestamp = chainSpecJson.Params.Eip3855TransitionTimestamp,
            Eip3860Transition = chainSpecJson.Params.Eip3860Transition,
            Eip3860TransitionTimestamp = chainSpecJson.Params.Eip3860TransitionTimestamp,
            Eip4895TransitionTimestamp = chainSpecJson.Params.Eip4895TransitionTimestamp,
            Eip4844TransitionTimestamp = chainSpecJson.Params.Eip4844TransitionTimestamp,
            Eip4844Transition = chainSpecJson.Params.Eip4844Transition,
            Eip2537TransitionTimestamp = chainSpecJson.Params.Eip2537TransitionTimestamp,
            Eip5656Transition = chainSpecJson.Params.Eip5656Transition,
            Eip5656TransitionTimestamp = chainSpecJson.Params.Eip5656TransitionTimestamp,
            Eip6780Transition = chainSpecJson.Params.Eip6780Transition,
            Eip6780TransitionTimestamp = chainSpecJson.Params.Eip6780TransitionTimestamp,
            Eip7951TransitionTimestamp = chainSpecJson.Params.Eip7951TransitionTimestamp,
            Rip7212TransitionTimestamp = chainSpecJson.Params.Rip7212TransitionTimestamp,
            OpGraniteTransitionTimestamp = chainSpecJson.Params.OpGraniteTransitionTimestamp,
            OpHoloceneTransitionTimestamp = chainSpecJson.Params.OpHoloceneTransitionTimestamp,
            OpIsthmusTransitionTimestamp = chainSpecJson.Params.OpIsthmusTransitionTimestamp,
            Eip4788TransitionTimestamp = chainSpecJson.Params.Eip4788TransitionTimestamp,
            Eip7702TransitionTimestamp = chainSpecJson.Params.Eip7702TransitionTimestamp,
            Eip7918TransitionTimestamp = chainSpecJson.Params.Eip7918TransitionTimestamp,
            Eip7823TransitionTimestamp = chainSpecJson.Params.Eip7823TransitionTimestamp,
            Eip7825TransitionTimestamp = chainSpecJson.Params.Eip7825TransitionTimestamp,
            Eip4788ContractAddress = chainSpecJson.Params.Eip4788ContractAddress ?? Eip4788Constants.BeaconRootsAddress,
            Eip2935TransitionTimestamp = chainSpecJson.Params.Eip2935TransitionTimestamp,
            Eip2935ContractAddress = chainSpecJson.Params.Eip2935ContractAddress ?? Eip2935Constants.BlockHashHistoryAddress,
            Eip2935RingBufferSize = chainSpecJson.Params.Eip2935RingBufferSize ?? Eip2935Constants.RingBufferSize,
            TransactionPermissionContract = chainSpecJson.Params.TransactionPermissionContract,
            TransactionPermissionContractTransition = chainSpecJson.Params.TransactionPermissionContractTransition,
            ValidateChainIdTransition = chainSpecJson.Params.ValidateChainIdTransition,
            ValidateReceiptsTransition = chainSpecJson.Params.ValidateReceiptsTransition,
            Eip1559ElasticityMultiplier = chainSpecJson.Params.Eip1559ElasticityMultiplier ?? Eip1559Constants.DefaultElasticityMultiplier,
            Eip1559BaseFeeInitialValue = chainSpecJson.Params.Eip1559BaseFeeInitialValue ?? Eip1559Constants.DefaultForkBaseFee,
            Eip1559BaseFeeMaxChangeDenominator = chainSpecJson.Params.Eip1559BaseFeeMaxChangeDenominator ??
                                                 Eip1559Constants.DefaultBaseFeeMaxChangeDenominator,

            Eip6110TransitionTimestamp = chainSpecJson.Params.Eip6110TransitionTimestamp,
            DepositContractAddress = LoadDependentParam(chainSpecJson.Params.Eip6110TransitionTimestamp, chainSpecJson.Params.DepositContractAddress,
                () => chainSpecJson.Params.ChainId == BlockchainIds.Mainnet ? Eip6110Constants.MainnetDepositContractAddress : null),
            Eip7002TransitionTimestamp = chainSpecJson.Params.Eip7002TransitionTimestamp,
            Eip7623TransitionTimestamp = chainSpecJson.Params.Eip7623TransitionTimestamp,
            Eip7976TransitionTimestamp = chainSpecJson.Params.Eip7976TransitionTimestamp,
            Eip7981TransitionTimestamp = chainSpecJson.Params.Eip7981TransitionTimestamp,
            Eip7883TransitionTimestamp = chainSpecJson.Params.Eip7883TransitionTimestamp,
            Eip7002ContractAddress = chainSpecJson.Params.Eip7002ContractAddress ?? Eip7002Constants.WithdrawalRequestPredeployAddress,
            Eip7251TransitionTimestamp = chainSpecJson.Params.Eip7251TransitionTimestamp,
            Eip7251ContractAddress = chainSpecJson.Params.Eip7251ContractAddress ?? Eip7251Constants.ConsolidationRequestPredeployAddress,
            FeeCollector = chainSpecJson.Params.FeeCollector,
            Eip1559FeeCollectorTransition = chainSpecJson.Params.Eip1559FeeCollectorTransition,
            Eip1559BaseFeeMinValueTransition = chainSpecJson.Params.Eip1559BaseFeeMinValueTransition,
            Eip1559BaseFeeMinValue = chainSpecJson.Params.Eip1559BaseFeeMinValue,
            Eip4844BlobGasPriceUpdateFraction = chainSpecJson.Params.Eip4844BlobGasPriceUpdateFraction,
            Eip4844MinBlobGasPrice = chainSpecJson.Params.Eip4844MinBlobGasPrice,
            Eip4844FeeCollectorTransitionTimestamp = chainSpecJson.Params.Eip4844FeeCollectorTransitionTimestamp,
            MergeForkIdTransition = chainSpecJson.Params.MergeForkIdTransition,
            TerminalTotalDifficulty = chainSpecJson.Params.TerminalTotalDifficulty,
            TerminalPoWBlockNumber = chainSpecJson.Params.TerminalPoWBlockNumber,
            BlobSchedule = chainSpecJson.Params.BlobSchedule,

            Eip7594TransitionTimestamp = chainSpecJson.Params.Eip7594TransitionTimestamp,
            Eip7939TransitionTimestamp = chainSpecJson.Params.Eip7939TransitionTimestamp,

            Eip7934TransitionTimestamp = chainSpecJson.Params.Eip7934TransitionTimestamp,
            Eip7934MaxRlpBlockSize = chainSpecJson.Params.Eip7934MaxRlpBlockSize ?? Eip7934Constants.DefaultMaxRlpBlockSize,

            Eip7778TransitionTimestamp = chainSpecJson.Params.Eip7778TransitionTimestamp,
            Eip8037TransitionTimestamp = chainSpecJson.Params.Eip8037TransitionTimestamp,

            Eip7928TransitionTimestamp = chainSpecJson.Params.Eip7928TransitionTimestamp,
            Eip7708TransitionTimestamp = chainSpecJson.Params.Eip7708TransitionTimestamp,

            Eip8024TransitionTimestamp = chainSpecJson.Params.Eip8024TransitionTimestamp,
            Eip7843TransitionTimestamp = chainSpecJson.Params.Eip7843TransitionTimestamp,
            Eip7954TransitionTimestamp = chainSpecJson.Params.Eip7954TransitionTimestamp,
        };

        chainSpec.Parameters.Eip152Transition ??= GetTransitionForExpectedPricing("blake2_f", "price.blake2_f.gas_per_round", 1);
        chainSpec.Parameters.Eip1108Transition ??= GetTransitionForExpectedPricing("alt_bn128_add", "price.alt_bn128_const_operations.price", 150)
                                                   ?? GetTransitionForExpectedPricing("alt_bn128_mul", "price.alt_bn128_const_operations.price", 6000)
                                                   ?? GetTransitionForExpectedPricing("alt_bn128_pairing", "price.alt_bn128_pairing.base", 45000);
        chainSpec.Parameters.Eip2565Transition ??= GetTransitionIfInnerPathExists("modexp", "price.modexp2565");

        Eip4844Constants.OverrideIfAny(chainSpec.Parameters.Eip4844MinBlobGasPrice);
    }

    internal static TValue? LoadDependentParam<TTransition, TValue>(
        TTransition? transition,
        TValue? value,
        Func<TValue?>? fallback = null,
        [CallerArgumentExpression("transition")] string transitionPropertyName = "",
        [CallerArgumentExpression("value")] string valuePropertyName = "")
        where TTransition : struct, IBinaryInteger<TTransition> =>
        transition is not null
            ? value is null
                ? (fallback is not null ? fallback() : default) ?? throw new InvalidConfigurationException(
                    $"Chainspec contains configuration for {transitionPropertyName}, but doesn't contain it for connected parameter {valuePropertyName}",
                    ExitCodes.MissingChainspecEipConfiguration)
                : value
            : default;

    /// <summary>
    /// Expands hardfork shorthand labels (Shanghai/Cancun/Prague/Osaka) into their constituent
    /// per-EIP transition timestamps. A label and an explicit EIP timestamp set to different
    /// values is rejected.
    /// </summary>
    /// <remarks>
    /// Resolves at JSON-load time so the rest of the pipeline keeps reading individual
    /// <c>EipXxxTransitionTimestamp</c> fields and stays unaware of the shorthand.
    /// </remarks>
    private static void ExpandHardforkLabels(ChainSpecParamsJson p)
    {
        if (p.Shanghai is { } shanghai)
        {
            ApplyHardforkLabel(nameof(p.Shanghai), shanghai,
                (nameof(p.Eip3651TransitionTimestamp), p.Eip3651TransitionTimestamp, v => p.Eip3651TransitionTimestamp = v),
                (nameof(p.Eip3855TransitionTimestamp), p.Eip3855TransitionTimestamp, v => p.Eip3855TransitionTimestamp = v),
                (nameof(p.Eip3860TransitionTimestamp), p.Eip3860TransitionTimestamp, v => p.Eip3860TransitionTimestamp = v),
                (nameof(p.Eip4895TransitionTimestamp), p.Eip4895TransitionTimestamp, v => p.Eip4895TransitionTimestamp = v));
        }

        if (p.Cancun is { } cancun)
        {
            ApplyHardforkLabel(nameof(p.Cancun), cancun,
                (nameof(p.Eip1153TransitionTimestamp), p.Eip1153TransitionTimestamp, v => p.Eip1153TransitionTimestamp = v),
                (nameof(p.Eip4788TransitionTimestamp), p.Eip4788TransitionTimestamp, v => p.Eip4788TransitionTimestamp = v),
                (nameof(p.Eip4844TransitionTimestamp), p.Eip4844TransitionTimestamp, v => p.Eip4844TransitionTimestamp = v),
                (nameof(p.Eip5656TransitionTimestamp), p.Eip5656TransitionTimestamp, v => p.Eip5656TransitionTimestamp = v),
                (nameof(p.Eip6780TransitionTimestamp), p.Eip6780TransitionTimestamp, v => p.Eip6780TransitionTimestamp = v));
        }

        if (p.Prague is { } prague)
        {
            ApplyHardforkLabel(nameof(p.Prague), prague,
                (nameof(p.Eip2537TransitionTimestamp), p.Eip2537TransitionTimestamp, v => p.Eip2537TransitionTimestamp = v),
                (nameof(p.Eip2935TransitionTimestamp), p.Eip2935TransitionTimestamp, v => p.Eip2935TransitionTimestamp = v),
                (nameof(p.Eip6110TransitionTimestamp), p.Eip6110TransitionTimestamp, v => p.Eip6110TransitionTimestamp = v),
                (nameof(p.Eip7002TransitionTimestamp), p.Eip7002TransitionTimestamp, v => p.Eip7002TransitionTimestamp = v),
                (nameof(p.Eip7251TransitionTimestamp), p.Eip7251TransitionTimestamp, v => p.Eip7251TransitionTimestamp = v),
                (nameof(p.Eip7623TransitionTimestamp), p.Eip7623TransitionTimestamp, v => p.Eip7623TransitionTimestamp = v),
                (nameof(p.Eip7702TransitionTimestamp), p.Eip7702TransitionTimestamp, v => p.Eip7702TransitionTimestamp = v));
        }

        if (p.Osaka is { } osaka)
        {
            ApplyHardforkLabel(nameof(p.Osaka), osaka,
                (nameof(p.Eip7594TransitionTimestamp), p.Eip7594TransitionTimestamp, v => p.Eip7594TransitionTimestamp = v),
                (nameof(p.Eip7823TransitionTimestamp), p.Eip7823TransitionTimestamp, v => p.Eip7823TransitionTimestamp = v),
                (nameof(p.Eip7825TransitionTimestamp), p.Eip7825TransitionTimestamp, v => p.Eip7825TransitionTimestamp = v),
                (nameof(p.Eip7883TransitionTimestamp), p.Eip7883TransitionTimestamp, v => p.Eip7883TransitionTimestamp = v),
                (nameof(p.Eip7918TransitionTimestamp), p.Eip7918TransitionTimestamp, v => p.Eip7918TransitionTimestamp = v),
                (nameof(p.Eip7934TransitionTimestamp), p.Eip7934TransitionTimestamp, v => p.Eip7934TransitionTimestamp = v),
                (nameof(p.Eip7939TransitionTimestamp), p.Eip7939TransitionTimestamp, v => p.Eip7939TransitionTimestamp = v),
                (nameof(p.Eip7951TransitionTimestamp), p.Eip7951TransitionTimestamp, v => p.Eip7951TransitionTimestamp = v));
        }
    }

    private static void ApplyHardforkLabel(string label, ulong labelValue, params (string Name, ulong? Current, Action<ulong?> Setter)[] eips)
    {
        foreach ((string name, ulong? current, Action<ulong?> setter) in eips)
        {
            if (current is null)
                setter(labelValue);
            else if (current.Value != labelValue)
                throw new InvalidConfigurationException(
                    $"Chainspec hardfork label '{label}' = 0x{labelValue:x} conflicts with explicit {name} = 0x{current.Value:x}. Either remove the conflicting field or align both values.",
                    ExitCodes.MissingChainspecEipConfiguration);
        }
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
        chainSpec.HomesteadBlockNumber = 0;
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
        chainSpec.BerlinBlockNumber = chainSpec.Parameters.Eip2929Transition;
        chainSpec.LondonBlockNumber = chainSpec.Parameters.Eip1559Transition;
        chainSpec.ShanghaiTimestamp = chainSpec.Parameters.Eip3651TransitionTimestamp;
        chainSpec.CancunTimestamp = chainSpec.Parameters.Eip4844TransitionTimestamp;
        chainSpec.PragueTimestamp = chainSpec.Parameters.Eip7002TransitionTimestamp;
        chainSpec.OsakaTimestamp = chainSpec.Parameters.Eip7594TransitionTimestamp;
        chainSpec.AmsterdamTimestamp = chainSpec.Parameters.Eip7928TransitionTimestamp;

        // TheMerge parameters
        chainSpec.MergeForkIdBlockNumber = chainSpec.Parameters.MergeForkIdTransition;
        chainSpec.TerminalPoWBlockNumber = chainSpec.Parameters.TerminalPoWBlockNumber;
        chainSpec.TerminalTotalDifficulty = chainSpec.Parameters.TerminalTotalDifficulty;


        if (chainSpec.EngineChainSpecParametersProvider is not null)
        {
            foreach (IChainSpecEngineParameters chainSpecEngineParameters in chainSpec.EngineChainSpecParametersProvider
                         .AllChainSpecParameters)
            {
                chainSpecEngineParameters.ApplyToChainSpec(chainSpec);
            }
        }
    }

    private void LoadEngine(ChainSpecJson chainSpecJson, ChainSpec chainSpec)
    {
        Dictionary<string, JsonElement> engineParameters = chainSpecJson.Engine.CustomEngineData.ToDictionary(
            engine => engine.Key,
            engine => engine.Value.TryGetProperty("params", out JsonElement value) ? value : engine.Value);

        chainSpec.EngineChainSpecParametersProvider = new ChainSpecParametersProvider(engineParameters, serializer);
        if (string.IsNullOrEmpty(chainSpec.SealEngineType))
        {
            chainSpec.SealEngineType = chainSpec.EngineChainSpecParametersProvider.SealEngineType;
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
        byte[] extraData = chainSpecJson.Genesis.ExtraData ?? [];
        UInt256 gasLimit = chainSpecJson.Genesis.GasLimit;
        Address beneficiary = chainSpecJson.Genesis.Author ?? Address.Zero;
        UInt256 baseFee = chainSpecJson.Params.Eip1559Transition switch
        {
            null => chainSpecJson.Genesis.BaseFeePerGas ?? UInt256.Zero,
            0 => chainSpecJson.Genesis.BaseFeePerGas ?? Eip1559Constants.DefaultForkBaseFee,
            _ => UInt256.Zero,
        };


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
            extraData)
        {
            Author = beneficiary,
            Hash = Keccak.Zero, // need to run the block to know the actual hash
            Bloom = Bloom.Empty,
            MixHash = mixHash,
            Nonce = (ulong)nonce,
            ReceiptsRoot = Keccak.EmptyTreeHash,
            StateRoot = stateRoot,
            TxRoot = Keccak.EmptyTreeHash,
            BaseFeePerGas = baseFee
        };

        bool withdrawalsEnabled = chainSpecJson.Params.Eip4895TransitionTimestamp is not null && genesisHeader.Timestamp >= chainSpecJson.Params.Eip4895TransitionTimestamp;
        bool depositsEnabled = chainSpecJson.Params.Eip6110TransitionTimestamp is not null && genesisHeader.Timestamp >= chainSpecJson.Params.Eip6110TransitionTimestamp;
        bool withdrawalRequestsEnabled = chainSpecJson.Params.Eip7002TransitionTimestamp is not null && genesisHeader.Timestamp >= chainSpecJson.Params.Eip7002TransitionTimestamp;
        bool consolidationRequestsEnabled = chainSpecJson.Params.Eip7251TransitionTimestamp is not null && genesisHeader.Timestamp >= chainSpecJson.Params.Eip7251TransitionTimestamp;
        bool blockAccessListsEnabled = chainSpecJson.Params.Eip7928TransitionTimestamp is not null && genesisHeader.Timestamp >= chainSpecJson.Params.Eip7928TransitionTimestamp;
        bool slotNumberEnabled = chainSpecJson.Params.Eip7843TransitionTimestamp is not null && genesisHeader.Timestamp >= chainSpecJson.Params.Eip7843TransitionTimestamp;

        if (withdrawalsEnabled)
        {
            genesisHeader.WithdrawalsRoot = Keccak.EmptyTreeHash;
        }

        bool requestsEnabled = depositsEnabled || withdrawalRequestsEnabled || consolidationRequestsEnabled;
        if (requestsEnabled)
        {
            genesisHeader.RequestsHash = ExecutionRequestExtensions.EmptyRequestsHash;
        }

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

        if (requestsEnabled)
        {
            genesisHeader.ReceiptsRoot = Keccak.EmptyTreeHash;
        }

        if (blockAccessListsEnabled)
        {
            genesisHeader.BlockAccessListHash = Keccak.OfAnEmptySequenceRlp;
        }

        if (slotNumberEnabled)
        {
            genesisHeader.SlotNumber = 0;
        }

        genesisHeader.AuRaStep = step;
        genesisHeader.AuRaSignature = auRaSignature;

        chainSpec.Genesis = !blockAccessListsEnabled ?
            (!withdrawalsEnabled
                ? new Block(genesisHeader)
                : new Block(genesisHeader, [], [], []))
            : new Block(genesisHeader, [], [], [], new());
    }

    private static void LoadAllocations(ChainSpecJson chainSpecJson, ChainSpec chainSpec)
    {
        if (chainSpecJson.Accounts is null)
        {
            return;
        }

        if (chainSpecJson.CodeHashes is not null)
        {
            foreach (KeyValuePair<string, byte[]> codeHash in chainSpecJson.CodeHashes)
            {
                if (ValueKeccak.Compute(codeHash.Value) != new ValueHash256(codeHash.Key)) throw new ArgumentException($"Unexpected code {codeHash.Key}");
            }
            chainSpecJson.CodeHashes[Hash256.Zero.ToString()] = [];
        }

        chainSpec.Allocations = new Dictionary<Address, ChainSpecAllocation>();
        foreach (KeyValuePair<string, AllocationJson> account in chainSpecJson.Accounts)
        {
            if (account.Value.BuiltIn is not null && account.Value.Balance is null)
            {
                continue;
            }

            if (account.Value.CodeHash is not null && account.Value.Code is not null)
            {
                throw new ArgumentException("CodeHash and Code are both not null");
            }

            Address address = new(account.Key);

            if (account.Value.CodeHash is not null)
            {
                string codeHashString = account.Value.CodeHash.ToString();
                if (chainSpecJson.CodeHashes is null || !chainSpecJson.CodeHashes.TryGetValue(codeHashString, out byte[] codeHash)) throw new ArgumentException($"CodeHash {account.Value.CodeHash} is not found");
                chainSpec.Allocations[address] = new ChainSpecAllocation(
                    account.Value.Balance ?? UInt256.Zero,
                    account.Value.Nonce,
                    codeHash,
                    account.Value.Constructor,
                    account.Value.GetConvertedStorage());
            }
            else
            {
                chainSpec.Allocations[address] = new ChainSpecAllocation(
                    account.Value.Balance ?? UInt256.Zero,
                    account.Value.Nonce,
                    account.Value.Code,
                    account.Value.Constructor,
                    account.Value.GetConvertedStorage());
            }
        }
    }

    private void LoadBootnodes(ChainSpecJson chainSpecJson, ChainSpec chainSpec)
        => chainSpec.Bootnodes = NetworkNode.ParseNodes(chainSpecJson.Nodes, _logger);
}
