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
            ChainSpecJson? chainSpecJson = serializer.Deserialize<ChainSpecJson>(streamData);
            ArgumentNullException.ThrowIfNull(chainSpecJson);
            return InitChainSpecFrom(chainSpecJson);
        }
        catch (Exception e)
        {
            throw new InvalidDataException($"Error when loading chainspec ({e.Message})", e);
        }
    }

    private ChainSpec InitChainSpecFrom(ChainSpecJson chainSpecJson)
    {
        ChainSpecParamsJson parameters = chainSpecJson.Params
            ?? throw new ArgumentNullException(nameof(chainSpecJson.Params));
        ChainSpecJson.EngineJson engine = chainSpecJson.Engine
            ?? throw new ArgumentNullException(nameof(chainSpecJson.Engine));

        ulong networkId = parameters.NetworkId ?? parameters.ChainId ?? 1;
        ChainSpec chainSpec = new()
        {
            NetworkId = networkId,
            ChainId = parameters.ChainId ?? networkId,
            Name = chainSpecJson.Name,
            DataDir = chainSpecJson.DataDir
        };

        // LoadGenesis reads chainSpec.Parameters, which LoadParameters populates and label-expands.
        LoadParameters(chainSpecJson, parameters, chainSpec);
        LoadGenesis(chainSpecJson, chainSpec);
        LoadEngine(engine, chainSpec);
        LoadAllocations(chainSpecJson, chainSpec);
        LoadBootnodes(chainSpecJson, chainSpec);
        LoadTransitions(chainSpecJson, chainSpec);

        return chainSpec;
    }

    private void LoadParameters(ChainSpecJson chainSpecJson, ChainSpecParamsJson parameters, ChainSpec chainSpec)
    {
        ulong? GetTransitions(string builtInName, Predicate<KeyValuePair<string, JsonElement>> predicate)
        {
            AllocationJson? allocation = chainSpecJson.Accounts?.Values.FirstOrDefault(v =>
                v.BuiltIn is { Name: { } name } && name.Equals(builtInName, StringComparison.OrdinalIgnoreCase));
            if (allocation is null) return null;
            BuiltInJson? builtIn = allocation.BuiltIn;
            if (builtIn is null)
            {
                return null;
            }

            if (builtIn.Pricing is not { } builtInPricing) return null;
            KeyValuePair<string, JsonElement>[] pricing = builtInPricing.Where(o => predicate(o)).ToArray();
            if (pricing.Length > 0)
            {
                string key = pricing[0].Key;
                return ulong.TryParse(key, out ulong transition) ? transition : Convert.ToUInt64(key, 16);
            }

            return null;
        }

        ulong? GetTransitionForExpectedPricing(string builtInName, string innerPath, long expectedValue)
        {
            bool GetForExpectedPricing(KeyValuePair<string, JsonElement> o) =>
                o.Value.TryGetSubProperty(innerPath, out JsonElement value) && value.GetInt64() == expectedValue;

            return GetTransitions(builtInName, GetForExpectedPricing);
        }

        ulong? GetTransitionIfInnerPathExists(string builtInName, string innerPath)
        {
            bool GetForInnerPathExistence(KeyValuePair<string, JsonElement> o) =>
                o.Value.TryGetSubProperty(innerPath, out _);

            return GetTransitions(builtInName, GetForInnerPathExistence);
        }

        chainSpec.Parameters = new ChainParameters
        {
            GasLimitBoundDivisor = parameters.GasLimitBoundDivisor ?? 0x0400UL,
            MaximumExtraDataSize = parameters.MaximumExtraDataSize ?? 32,
            MinGasLimit = parameters.MinGasLimit ?? 5000UL,
            MinHistoryRetentionEpochs = parameters.MinHistoryRetentionEpochs ?? HistoryRetentionConstants.MinEpochsForBlockRequests,
            MinBalRetentionEpochs = parameters.MinBalRetentionEpochs ?? HistoryRetentionConstants.WeakSubjectivityPeriodEpochs,
            MaxCodeSize = parameters.MaxCodeSize,
            MaxCodeSizeTransition = parameters.MaxCodeSizeTransition,
            MaxCodeSizeTransitionTimestamp = parameters.MaxCodeSizeTransitionTimestamp,
            Registrar = parameters.Registrar,
            ForkBlock = parameters.ForkBlock,
            ForkCanonHash = parameters.ForkCanonHash,
            Eip7Transition = parameters.Eip7Transition,
            Eip150Transition = parameters.Eip150Transition,
            Eip152Transition = parameters.Eip152Transition,
            Eip160Transition = parameters.Eip160Transition,
            Eip161abcTransition = parameters.Eip161abcTransition,
            Eip161dTransition = parameters.Eip161dTransition,
            Eip155Transition = parameters.Eip155Transition,
            Eip140Transition = parameters.Eip140Transition,
            Eip211Transition = parameters.Eip211Transition,
            Eip214Transition = parameters.Eip214Transition,
            Eip658Transition = parameters.Eip658Transition,
            Eip145Transition = parameters.Eip145Transition,
            Eip1014Transition = parameters.Eip1014Transition,
            Eip1052Transition = parameters.Eip1052Transition,
            Eip1108Transition = parameters.Eip1108Transition,
            Eip1283Transition = parameters.Eip1283Transition,
            Eip1283DisableTransition = parameters.Eip1283DisableTransition,
            Eip1283ReenableTransition = parameters.Eip1283ReenableTransition,
            Eip1344Transition = parameters.Eip1344Transition,
            Eip1706Transition = parameters.Eip1706Transition,
            Eip1884Transition = parameters.Eip1884Transition,
            Eip2028Transition = parameters.Eip2028Transition,
            Eip2200Transition = parameters.Eip2200Transition,
            Eip1559Transition = parameters.Eip1559Transition,
            Eip2315Transition = parameters.Eip2315Transition,
            Eip2537Transition = parameters.Eip2537Transition,
            Eip2565Transition = parameters.Eip2565Transition,
            Eip2929Transition = parameters.Eip2929Transition,
            Eip2930Transition = parameters.Eip2930Transition,
            Eip3198Transition = parameters.Eip3198Transition,
            Eip3541Transition = parameters.Eip3541Transition,
            Eip3529Transition = parameters.Eip3529Transition,
            Eip3607Transition = parameters.Eip3607Transition,
            BeaconChainGenesisTimestamp = parameters.BeaconChainGenesisTimestamp,
            Eip1153Transition = parameters.Eip1153Transition,
            Eip1153TransitionTimestamp = parameters.Eip1153TransitionTimestamp,
            Eip3651Transition = parameters.Eip3651Transition,
            Eip3651TransitionTimestamp = parameters.Eip3651TransitionTimestamp,
            Eip3855Transition = parameters.Eip3855Transition,
            Eip3855TransitionTimestamp = parameters.Eip3855TransitionTimestamp,
            Eip3860Transition = parameters.Eip3860Transition,
            Eip3860TransitionTimestamp = parameters.Eip3860TransitionTimestamp,
            Eip4895TransitionTimestamp = parameters.Eip4895TransitionTimestamp,
            Eip4844TransitionTimestamp = parameters.Eip4844TransitionTimestamp,
            Eip4844Transition = parameters.Eip4844Transition,
            Eip2537TransitionTimestamp = parameters.Eip2537TransitionTimestamp,
            Eip5656Transition = parameters.Eip5656Transition,
            Eip5656TransitionTimestamp = parameters.Eip5656TransitionTimestamp,
            Eip6780Transition = parameters.Eip6780Transition,
            Eip6780TransitionTimestamp = parameters.Eip6780TransitionTimestamp,
            Eip7951TransitionTimestamp = parameters.Eip7951TransitionTimestamp,
            Rip7212TransitionTimestamp = parameters.Rip7212TransitionTimestamp,
            Eip4788TransitionTimestamp = parameters.Eip4788TransitionTimestamp,
            Eip7702Transition = parameters.Eip7702Transition,
            Eip7702TransitionTimestamp = parameters.Eip7702TransitionTimestamp,
            Eip7918TransitionTimestamp = parameters.Eip7918TransitionTimestamp,
            Eip7823TransitionTimestamp = parameters.Eip7823TransitionTimestamp,
            Eip7825TransitionTimestamp = parameters.Eip7825TransitionTimestamp,
            Eip4788ContractAddress = parameters.Eip4788ContractAddress ?? Eip4788Constants.BeaconRootsAddress,
            Eip2935Transition = parameters.Eip2935Transition,
            Eip2935TransitionTimestamp = parameters.Eip2935TransitionTimestamp,
            Eip2935ContractAddress = parameters.Eip2935ContractAddress ?? Eip2935Constants.BlockHashHistoryAddress,
            Eip2935RingBufferSize = parameters.Eip2935RingBufferSize ?? Eip2935Constants.RingBufferSize,
            TransactionPermissionContract = parameters.TransactionPermissionContract,
            TransactionPermissionContractTransition = parameters.TransactionPermissionContractTransition,
            ValidateChainIdTransition = parameters.ValidateChainIdTransition,
            ValidateReceiptsTransition = parameters.ValidateReceiptsTransition,
            Eip1559ElasticityMultiplier = parameters.Eip1559ElasticityMultiplier ?? Eip1559Constants.DefaultElasticityMultiplier,
            Eip1559BaseFeeInitialValue = parameters.Eip1559BaseFeeInitialValue ?? Eip1559Constants.DefaultForkBaseFee,
            Eip1559BaseFeeMaxChangeDenominator = parameters.Eip1559BaseFeeMaxChangeDenominator ??
                                                 Eip1559Constants.DefaultBaseFeeMaxChangeDenominator,

            Eip6110TransitionTimestamp = parameters.Eip6110TransitionTimestamp,
            DepositContractAddress = LoadDependentParam(parameters.Eip6110TransitionTimestamp, parameters.DepositContractAddress,
                () => parameters.ChainId == BlockchainIds.Mainnet ? Eip6110Constants.MainnetDepositContractAddress : null),
            Eip7002TransitionTimestamp = parameters.Eip7002TransitionTimestamp,
            Eip7623Transition = parameters.Eip7623Transition,
            Eip7623TransitionTimestamp = parameters.Eip7623TransitionTimestamp,
            Eip7976TransitionTimestamp = parameters.Eip7976TransitionTimestamp,
            Eip7981TransitionTimestamp = parameters.Eip7981TransitionTimestamp,
            Eip7883TransitionTimestamp = parameters.Eip7883TransitionTimestamp,
            Eip7002ContractAddress = parameters.Eip7002ContractAddress ?? Eip7002Constants.WithdrawalRequestPredeployAddress,
            Eip7251TransitionTimestamp = parameters.Eip7251TransitionTimestamp,
            Eip7251ContractAddress = parameters.Eip7251ContractAddress ?? Eip7251Constants.ConsolidationRequestPredeployAddress,
            FeeCollector = parameters.FeeCollector,
            Eip1559FeeCollectorTransition = parameters.Eip1559FeeCollectorTransition,
            Eip1559BaseFeeMinValueTransition = parameters.Eip1559BaseFeeMinValueTransition,
            Eip1559BaseFeeMinValue = parameters.Eip1559BaseFeeMinValue,
            Eip4844BlobGasPriceUpdateFraction = parameters.Eip4844BlobGasPriceUpdateFraction,
            Eip4844MinBlobGasPrice = parameters.Eip4844MinBlobGasPrice,
            Eip4844FeeCollectorTransitionTimestamp = parameters.Eip4844FeeCollectorTransitionTimestamp,
            MergeForkIdTransition = parameters.MergeForkIdTransition,
            TerminalTotalDifficulty = parameters.TerminalTotalDifficulty,
            TerminalPoWBlockNumber = parameters.TerminalPoWBlockNumber,
            BlobSchedule = parameters.BlobSchedule,

            Eip7594TransitionTimestamp = parameters.Eip7594TransitionTimestamp,
            Eip7939TransitionTimestamp = parameters.Eip7939TransitionTimestamp,

            Eip7934TransitionTimestamp = parameters.Eip7934TransitionTimestamp,
            Eip7934MaxRlpBlockSize = parameters.Eip7934MaxRlpBlockSize ?? Eip7934Constants.DefaultMaxRlpBlockSize,

            Eip7778TransitionTimestamp = parameters.Eip7778TransitionTimestamp,
            Eip8037TransitionTimestamp = parameters.Eip8037TransitionTimestamp,

            Eip7928TransitionTimestamp = parameters.Eip7928TransitionTimestamp,
            Eip7708TransitionTimestamp = parameters.Eip7708TransitionTimestamp,

            Eip8024TransitionTimestamp = parameters.Eip8024TransitionTimestamp,
            Eip8246TransitionTimestamp = parameters.Eip8246TransitionTimestamp,
            Eip8038TransitionTimestamp = parameters.Eip8038TransitionTimestamp,
            Eip8282TransitionTimestamp = parameters.Eip8282TransitionTimestamp,
            Eip7843TransitionTimestamp = parameters.Eip7843TransitionTimestamp,
            Eip7954TransitionTimestamp = parameters.Eip7954TransitionTimestamp,
            Eip2780TransitionTimestamp = parameters.Eip2780TransitionTimestamp,
            Eip7805TransitionTimestamp = parameters.Eip7805TransitionTimestamp,
        };

        chainSpec.Parameters.ExpandAll(parameters);
        ValidateParams(chainSpec.Parameters);

        // Pre-Shanghai EIPs that are part of the genesis baseline for chains without explicit
        // transitions. Applied AFTER ExpandAll so an explicit chainspec field or fork label wins.
        chainSpec.Parameters.Eip150Transition ??= 0;
        chainSpec.Parameters.Eip160Transition ??= 0;
        chainSpec.Parameters.Eip161abcTransition ??= 0;
        chainSpec.Parameters.Eip161dTransition ??= 0;
        chainSpec.Parameters.Eip155Transition ??= 0;

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

    private static void ValidateParams(ChainParameters parameters)
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

    private void LoadEngine(ChainSpecJson.EngineJson engine, ChainSpec chainSpec)
    {
        Dictionary<string, JsonElement> engineParameters = engine.CustomEngineData.ToDictionary(
            engine => engine.Key,
            engine => engine.Value.TryGetProperty("params", out JsonElement value) ? value : engine.Value);

        chainSpec.EngineChainSpecParametersProvider = new ChainSpecParametersProvider(engineParameters, serializer);
        if (IsUnspecifiedSealEngine(chainSpec.SealEngineType))
        {
            chainSpec.SealEngineType = chainSpec.EngineChainSpecParametersProvider.SealEngineType;
        }

        if (IsUnspecifiedSealEngine(chainSpec.SealEngineType))
        {
            throw new NotSupportedException("unknown seal engine in chainspec");
        }
    }

    private static bool IsUnspecifiedSealEngine(string sealEngineType) =>
        string.IsNullOrEmpty(sealEngineType) || sealEngineType == Nethermind.Core.SealEngineType.None;

    private static void LoadGenesis(ChainSpecJson chainSpecJson, ChainSpec chainSpec)
    {
        if (chainSpecJson.Genesis is null)
        {
            return;
        }

        ulong nonce = chainSpecJson.Genesis.Seal?.Ethereum?.Nonce ?? 0UL;
        Hash256 mixHash = chainSpecJson.Genesis.Seal?.Ethereum?.MixHash ?? Keccak.Zero;

        // Engine-specific seal sections are stashed raw; the owning consensus plugin (e.g. AuRa)
        // upgrades Genesis.Header via its ChainSpec interceptor.
        chainSpec.CustomSeal = chainSpecJson.Genesis.Seal?.CustomSeal;

        Hash256 parentHash = chainSpecJson.Genesis.ParentHash ?? Keccak.Zero;
        ulong timestamp = chainSpecJson.Genesis.Timestamp;
        UInt256 difficulty = chainSpecJson.Genesis.Difficulty;
        byte[] extraData = chainSpecJson.Genesis.ExtraData ?? [];
        ulong gasLimit = chainSpecJson.Genesis.GasLimit;
        Address beneficiary = chainSpecJson.Genesis.Author ?? Address.Zero;
        ChainParameters parameters = chainSpec.Parameters;
        UInt256 baseFee = parameters.Eip1559Transition switch
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
            gasLimit,
            timestamp,
            extraData)
        {
            Author = beneficiary,
            Hash = Keccak.Zero, // need to run the block to know the actual hash
            Bloom = Bloom.Empty,
            MixHash = mixHash,
            Nonce = nonce,
            ReceiptsRoot = Keccak.EmptyTreeHash,
            StateRoot = stateRoot,
            TxRoot = Keccak.EmptyTreeHash,
            BaseFeePerGas = baseFee
        };

        bool withdrawalsEnabled = parameters.Eip4895TransitionTimestamp is not null && genesisHeader.Timestamp >= parameters.Eip4895TransitionTimestamp;
        bool depositsEnabled = parameters.Eip6110TransitionTimestamp is not null && genesisHeader.Timestamp >= parameters.Eip6110TransitionTimestamp;
        bool withdrawalRequestsEnabled = parameters.Eip7002TransitionTimestamp is not null && genesisHeader.Timestamp >= parameters.Eip7002TransitionTimestamp;
        bool consolidationRequestsEnabled = parameters.Eip7251TransitionTimestamp is not null && genesisHeader.Timestamp >= parameters.Eip7251TransitionTimestamp;
        bool blockAccessListsEnabled = parameters.Eip7928TransitionTimestamp is not null && genesisHeader.Timestamp >= parameters.Eip7928TransitionTimestamp;
        bool slotNumberEnabled = parameters.Eip7843TransitionTimestamp is not null && genesisHeader.Timestamp >= parameters.Eip7843TransitionTimestamp;

        if (withdrawalsEnabled)
        {
            genesisHeader.WithdrawalsRoot = Keccak.EmptyTreeHash;
        }

        bool requestsEnabled = depositsEnabled || withdrawalRequestsEnabled || consolidationRequestsEnabled;
        if (requestsEnabled)
        {
            genesisHeader.RequestsHash = ExecutionRequestExtensions.EmptyRequestsHash;
        }

        bool isEip4844Enabled = parameters.Eip4844TransitionTimestamp is not null && genesisHeader.Timestamp >= parameters.Eip4844TransitionTimestamp;
        if (isEip4844Enabled)
        {
            genesisHeader.BlobGasUsed = chainSpecJson.Genesis.BlobGasUsed;
            genesisHeader.ExcessBlobGas = chainSpecJson.Genesis.ExcessBlobGas;
        }

        bool isEip4788Enabled = parameters.Eip4788TransitionTimestamp is not null && genesisHeader.Timestamp >= parameters.Eip4788TransitionTimestamp;
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
            genesisHeader.SlotNumber = chainSpecJson.Genesis.SlotNumber ?? 0;
        }

        chainSpec.Genesis = !blockAccessListsEnabled ?
            (!withdrawalsEnabled
                ? new Block(genesisHeader)
                : new Block(genesisHeader, [], [], []))
            : new Block(genesisHeader, [], [], [], new());
    }

    private static void LoadAllocations(ChainSpecJson chainSpecJson, ChainSpec chainSpec)
    {
        chainSpec.Allocations = [];
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
                if (chainSpecJson.CodeHashes is null || !chainSpecJson.CodeHashes.TryGetValue(codeHashString, out byte[]? codeHash) || codeHash is null) throw new ArgumentException($"CodeHash {account.Value.CodeHash} is not found");
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
