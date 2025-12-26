// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle.Json;

namespace Nethermind.Specs.ChainSpecStyle;

/// <summary>
/// Loader for Geth-style genesis.json files as defined in EIP-7949.
/// Converts Geth-style genesis format to Nethermind's ChainSpec format.
/// </summary>
public class GethGenesisLoader(IJsonSerializer serializer) : IChainSpecLoader
{
    public ChainSpec Load(Stream streamData)
    {
        try
        {
            GethGenesisJson gethGenesis = serializer.Deserialize<GethGenesisJson>(streamData);
            return ConvertToChainSpec(gethGenesis);
        }
        catch (Exception e)
        {
            throw new InvalidDataException($"Error when loading Geth genesis file ({e.Message})", e);
        }
    }

    private ChainSpec ConvertToChainSpec(GethGenesisJson gethGenesis)
    {
        ArgumentNullException.ThrowIfNull(gethGenesis);
        ArgumentNullException.ThrowIfNull(gethGenesis.Config);

        ChainSpec chainSpec = new()
        {
            ChainId = gethGenesis.Config.ChainId,
            NetworkId = gethGenesis.Config.ChainId
        };

        LoadEngine(gethGenesis, chainSpec);
        LoadParameters(gethGenesis, chainSpec);
        LoadGenesis(gethGenesis, chainSpec);
        LoadAllocations(gethGenesis, chainSpec);
        LoadTransitions(chainSpec);

        return chainSpec;
    }

    private void LoadEngine(GethGenesisJson gethGenesis, ChainSpec chainSpec)
    {
        // Default to Ethash engine for Geth-style genesis files
        chainSpec.SealEngineType = SealEngineType.Ethash;
        chainSpec.EngineChainSpecParametersProvider = new GethGenesisEngineParametersProvider();
    }

    private static void LoadParameters(GethGenesisJson gethGenesis, ChainSpec chainSpec)
    {
        GethGenesisConfigJson config = gethGenesis.Config;
        ForkSettings forkSettings = ForkSettings.Instance;
        DefaultParametersJson defaults = forkSettings.Defaults;

        // Parse terminal total difficulty if present
        UInt256? terminalTotalDifficulty = null;
        if (!string.IsNullOrEmpty(config.TerminalTotalDifficulty))
        {
            terminalTotalDifficulty = ParseHexOrDecimal(config.TerminalTotalDifficulty);
        }

        // Build blob schedule from ForkSettings defaults, allowing chainspec overrides
        SortedSet<BlobScheduleSettings> blobSchedule = BuildBlobSchedule(config, forkSettings);

        // Build fork transition lookup tables
        var blockForks = BuildBlockForkTransitions(config);
        var timestampForks = BuildTimestampForkTransitions(config);

        chainSpec.Parameters = new ChainParameters
        {
            // Default parameters from ForkSettings
            GasLimitBoundDivisor = defaults.GasLimitBoundDivisor,
            MaximumExtraDataSize = defaults.MaximumExtraDataSize,
            MinGasLimit = defaults.MinGasLimit,
            MinHistoryRetentionEpochs = defaults.MinHistoryRetentionEpochs,
            MaxCodeSize = defaults.MaxCodeSize,
            MaxCodeSizeTransition = config.Eip158Block ?? config.SpuriousDragonBlock ?? 0,

            // Block-based EIP transitions - dynamically looks up which fork activates each EIP
            Eip150Transition = GetBlockTransitionForEip(forkSettings, blockForks, 150),
            Eip155Transition = GetBlockTransitionForEip(forkSettings, blockForks, 155),
            Eip160Transition = GetBlockTransitionForEip(forkSettings, blockForks, 160),
            Eip161abcTransition = GetBlockTransitionForEip(forkSettings, blockForks, 161),
            Eip161dTransition = GetBlockTransitionForEip(forkSettings, blockForks, 161),
            Eip140Transition = GetBlockTransitionForEip(forkSettings, blockForks, 140),
            Eip211Transition = GetBlockTransitionForEip(forkSettings, blockForks, 211),
            Eip214Transition = GetBlockTransitionForEip(forkSettings, blockForks, 214),
            Eip658Transition = GetBlockTransitionForEip(forkSettings, blockForks, 658),
            Eip145Transition = GetBlockTransitionForEip(forkSettings, blockForks, 145),
            Eip1014Transition = GetBlockTransitionForEip(forkSettings, blockForks, 1014),
            Eip1052Transition = GetBlockTransitionForEip(forkSettings, blockForks, 1052),
            Eip1283Transition = GetBlockTransitionForEip(forkSettings, blockForks, 1283),
            Eip1283DisableTransition = config.PetersburgBlock,
            Eip152Transition = GetBlockTransitionForEip(forkSettings, blockForks, 152),
            Eip1108Transition = GetBlockTransitionForEip(forkSettings, blockForks, 1108),
            Eip1344Transition = GetBlockTransitionForEip(forkSettings, blockForks, 1344),
            Eip1884Transition = GetBlockTransitionForEip(forkSettings, blockForks, 1884),
            Eip2028Transition = GetBlockTransitionForEip(forkSettings, blockForks, 2028),
            Eip2200Transition = GetBlockTransitionForEip(forkSettings, blockForks, 2200),
            Eip2565Transition = GetBlockTransitionForEip(forkSettings, blockForks, 2565),
            Eip2929Transition = GetBlockTransitionForEip(forkSettings, blockForks, 2929),
            Eip2930Transition = GetBlockTransitionForEip(forkSettings, blockForks, 2930),
            Eip1559Transition = GetBlockTransitionForEip(forkSettings, blockForks, 1559),
            Eip3198Transition = GetBlockTransitionForEip(forkSettings, blockForks, 3198),
            Eip3529Transition = GetBlockTransitionForEip(forkSettings, blockForks, 3529),
            Eip3541Transition = GetBlockTransitionForEip(forkSettings, blockForks, 3541),
            Eip3607Transition = GetBlockTransitionForEip(forkSettings, blockForks, 3607),

            MergeForkIdTransition = config.MergeNetsplitBlock,
            TerminalTotalDifficulty = terminalTotalDifficulty,

            // Timestamp-based EIP transitions - dynamically looks up which fork activates each EIP
            Eip3651TransitionTimestamp = GetTimestampTransitionForEip(forkSettings, timestampForks, 3651),
            Eip3855TransitionTimestamp = GetTimestampTransitionForEip(forkSettings, timestampForks, 3855),
            Eip3860TransitionTimestamp = GetTimestampTransitionForEip(forkSettings, timestampForks, 3860),
            Eip4895TransitionTimestamp = GetTimestampTransitionForEip(forkSettings, timestampForks, 4895),
            Eip1153TransitionTimestamp = GetTimestampTransitionForEip(forkSettings, timestampForks, 1153),
            Eip4844TransitionTimestamp = GetTimestampTransitionForEip(forkSettings, timestampForks, 4844),
            Eip4788TransitionTimestamp = GetTimestampTransitionForEip(forkSettings, timestampForks, 4788),
            Eip5656TransitionTimestamp = GetTimestampTransitionForEip(forkSettings, timestampForks, 5656),
            Eip6780TransitionTimestamp = GetTimestampTransitionForEip(forkSettings, timestampForks, 6780),
            Eip2537TransitionTimestamp = GetTimestampTransitionForEip(forkSettings, timestampForks, 2537),
            Eip2935TransitionTimestamp = GetTimestampTransitionForEip(forkSettings, timestampForks, 2935),
            Eip6110TransitionTimestamp = GetTimestampTransitionForEip(forkSettings, timestampForks, 6110),
            Eip7002TransitionTimestamp = GetTimestampTransitionForEip(forkSettings, timestampForks, 7002),
            Eip7251TransitionTimestamp = GetTimestampTransitionForEip(forkSettings, timestampForks, 7251),
            Eip7623TransitionTimestamp = GetTimestampTransitionForEip(forkSettings, timestampForks, 7623),
            Eip7702TransitionTimestamp = GetTimestampTransitionForEip(forkSettings, timestampForks, 7702),
            Eip7594TransitionTimestamp = GetTimestampTransitionForEip(forkSettings, timestampForks, 7594),
            Eip7823TransitionTimestamp = GetTimestampTransitionForEip(forkSettings, timestampForks, 7823),
            Eip7825TransitionTimestamp = GetTimestampTransitionForEip(forkSettings, timestampForks, 7825),
            Eip7883TransitionTimestamp = GetTimestampTransitionForEip(forkSettings, timestampForks, 7883),
            Eip7918TransitionTimestamp = GetTimestampTransitionForEip(forkSettings, timestampForks, 7918),
            Eip7934TransitionTimestamp = GetTimestampTransitionForEip(forkSettings, timestampForks, 7934),
            Eip7934MaxRlpBlockSize = defaults.MaxRlpBlockSize,
            Eip7939TransitionTimestamp = GetTimestampTransitionForEip(forkSettings, timestampForks, 7939),
            Eip7951TransitionTimestamp = GetTimestampTransitionForEip(forkSettings, timestampForks, 7951),

            // Contract addresses from ForkSettings, allowing chainspec overrides
            DepositContractAddress = config.DepositContractAddress ?? forkSettings.DepositContractAddress,
            Eip4788ContractAddress = forkSettings.BeaconRootsAddress,
            Eip2935ContractAddress = forkSettings.BlockHashHistoryAddress,
            Eip7002ContractAddress = forkSettings.WithdrawalRequestAddress,
            Eip7251ContractAddress = forkSettings.ConsolidationRequestAddress,
            BlobSchedule = blobSchedule
        };
    }

    /// <summary>
    /// Builds a dictionary mapping block-based fork names to their transition block numbers.
    /// </summary>
    private static Dictionary<string, long?> BuildBlockForkTransitions(GethGenesisConfigJson config) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["homestead"] = config.HomesteadBlock,
            ["tangerineWhistle"] = config.Eip150Block ?? config.TangerineWhistleBlock,
            ["spuriousDragon"] = config.Eip158Block ?? config.SpuriousDragonBlock,
            ["byzantium"] = config.ByzantiumBlock,
            ["constantinople"] = config.ConstantinopleBlock,
            ["petersburg"] = config.PetersburgBlock,
            ["istanbul"] = config.IstanbulBlock,
            ["berlin"] = config.BerlinBlock,
            ["london"] = config.LondonBlock
        };

    /// <summary>
    /// Builds a dictionary mapping timestamp-based fork names to their transition timestamps.
    /// </summary>
    private static Dictionary<string, ulong?> BuildTimestampForkTransitions(GethGenesisConfigJson config) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["shanghai"] = config.ShanghaiTime,
            ["cancun"] = config.CancunTime,
            ["prague"] = config.PragueTime,
            ["osaka"] = config.OsakaTime
        };

    /// <summary>
    /// Gets the block transition for an EIP by finding which fork first activates it.
    /// </summary>
    private static long? GetBlockTransitionForEip(
        ForkSettings forkSettings,
        Dictionary<string, long?> forkTransitions,
        int eipNumber)
    {
        string? activatingFork = forkSettings.GetActivatingFork(eipNumber);
        if (activatingFork is null)
        {
            return null;
        }

        return forkTransitions.GetValueOrDefault(activatingFork);
    }

    /// <summary>
    /// Gets the timestamp transition for an EIP by finding which fork first activates it.
    /// </summary>
    private static ulong? GetTimestampTransitionForEip(
        ForkSettings forkSettings,
        Dictionary<string, ulong?> forkTransitions,
        int eipNumber)
    {
        string? activatingFork = forkSettings.GetActivatingFork(eipNumber);
        if (activatingFork is null)
        {
            return null;
        }

        return forkTransitions.GetValueOrDefault(activatingFork);
    }

    /// <summary>
    /// Builds blob schedule from ForkSettings defaults, with chainspec overrides taking precedence.
    /// </summary>
    private static SortedSet<BlobScheduleSettings> BuildBlobSchedule(GethGenesisConfigJson config, ForkSettings forkSettings)
    {
        SortedSet<BlobScheduleSettings> blobSchedule = [];

        // Define fork names and their corresponding timestamps
        var forkTimestamps = new Dictionary<string, ulong?>
        {
            ["cancun"] = config.CancunTime,
            ["prague"] = config.PragueTime,
            ["osaka"] = config.OsakaTime,
            ["bpo1"] = GetBpo1Timestamp(config),
            ["bpo2"] = GetBpo2Timestamp(config)
        };

        foreach (var (forkName, timestamp) in forkTimestamps)
        {
            if (timestamp is null or 0)
            {
                continue;
            }

            // Check if chainspec has an override for this fork
            if (config.BlobSchedule is not null &&
                config.BlobSchedule.TryGetValue(forkName, out var chainspecEntry))
            {
                // Use chainspec values with explicit or looked-up timestamp
                ulong effectiveTimestamp = chainspecEntry.Timestamp ?? timestamp.Value;
                blobSchedule.Add(new BlobScheduleSettings
                {
                    Timestamp = effectiveTimestamp,
                    Target = chainspecEntry.Target,
                    Max = chainspecEntry.Max,
                    BaseFeeUpdateFraction = chainspecEntry.BaseFeeUpdateFraction
                });
            }
            else
            {
                // Use defaults from ForkSettings if this fork has blob schedule defined
                var defaultSchedule = forkSettings.GetBlobSchedule(forkName);
                if (defaultSchedule is not null)
                {
                    blobSchedule.Add(new BlobScheduleSettings
                    {
                        Timestamp = timestamp.Value,
                        Target = (ulong)defaultSchedule.Target,
                        Max = (ulong)defaultSchedule.Max,
                        BaseFeeUpdateFraction = defaultSchedule.BaseFeeUpdateFraction
                    });
                }
            }
        }

        return blobSchedule;
    }

    /// <summary>
    /// Gets BPO1 timestamp from config, looking in blobSchedule if not directly specified.
    /// </summary>
    private static ulong? GetBpo1Timestamp(GethGenesisConfigJson config)
    {
        if (config.BlobSchedule?.TryGetValue("bpo1", out var entry) == true && entry.Timestamp.HasValue)
        {
            return entry.Timestamp.Value;
        }

        return null;
    }

    /// <summary>
    /// Gets BPO2 timestamp from config, looking in blobSchedule if not directly specified.
    /// </summary>
    private static ulong? GetBpo2Timestamp(GethGenesisConfigJson config)
    {
        if (config.BlobSchedule?.TryGetValue("bpo2", out var entry) == true && entry.Timestamp.HasValue)
        {
            return entry.Timestamp.Value;
        }

        return null;
    }

    private static void LoadGenesis(GethGenesisJson gethGenesis, ChainSpec chainSpec)
    {
        UInt256 nonce = ParseHexOrDecimal(gethGenesis.Nonce ?? "0x0");
        Hash256 mixHash = string.IsNullOrEmpty(gethGenesis.MixHash)
            ? Keccak.Zero
            : new Hash256(Bytes.FromHexString(gethGenesis.MixHash));

        ulong timestamp = ParseHexOrDecimalULong(gethGenesis.Timestamp ?? "0x0");
        UInt256 difficulty = ParseHexOrDecimal(gethGenesis.Difficulty ?? "0x1");
        UInt256 gasLimit = ParseHexOrDecimal(gethGenesis.GasLimit ?? "0x1388");
        Address coinbase = string.IsNullOrEmpty(gethGenesis.Coinbase)
            ? Address.Zero
            : new Address(gethGenesis.Coinbase);

        byte[] extraData = string.IsNullOrEmpty(gethGenesis.ExtraData) || gethGenesis.ExtraData == ""
            ? []
            : Bytes.FromHexString(gethGenesis.ExtraData);

        UInt256 baseFee = UInt256.Zero;
        if (!string.IsNullOrEmpty(gethGenesis.BaseFeePerGas))
        {
            baseFee = ParseHexOrDecimal(gethGenesis.BaseFeePerGas);
        }
        else if (gethGenesis.Config.LondonBlock == 0)
        {
            baseFee = Eip1559Constants.DefaultForkBaseFee;
        }

        BlockHeader genesisHeader = new(
            Keccak.Zero,
            Keccak.OfAnEmptySequenceRlp,
            coinbase,
            difficulty,
            0,
            (long)gasLimit,
            timestamp,
            extraData);

        genesisHeader.Author = coinbase;
        genesisHeader.Hash = Keccak.Zero;
        genesisHeader.Bloom = Bloom.Empty;
        genesisHeader.MixHash = mixHash;
        genesisHeader.Nonce = (ulong)nonce;
        genesisHeader.ReceiptsRoot = Keccak.EmptyTreeHash;
        genesisHeader.StateRoot = Keccak.EmptyTreeHash;
        genesisHeader.TxRoot = Keccak.EmptyTreeHash;
        genesisHeader.BaseFeePerGas = baseFee;

        GethGenesisConfigJson config = gethGenesis.Config;

        bool withdrawalsEnabled = config.ShanghaiTime.HasValue && genesisHeader.Timestamp >= config.ShanghaiTime;
        if (withdrawalsEnabled)
        {
            genesisHeader.WithdrawalsRoot = Keccak.EmptyTreeHash;
        }

        bool isEip4844Enabled = config.CancunTime.HasValue && genesisHeader.Timestamp >= config.CancunTime;
        if (isEip4844Enabled)
        {
            genesisHeader.BlobGasUsed = ParseHexOrDecimalULong(gethGenesis.BlobGasUsed ?? "0x0");
            genesisHeader.ExcessBlobGas = ParseHexOrDecimalULong(gethGenesis.ExcessBlobGas ?? "0x0");
        }

        bool isEip4788Enabled = config.CancunTime.HasValue && genesisHeader.Timestamp >= config.CancunTime;
        if (isEip4788Enabled)
        {
            genesisHeader.ParentBeaconBlockRoot = string.IsNullOrEmpty(gethGenesis.ParentBeaconBlockRoot)
                ? Keccak.Zero
                : new Hash256(Bytes.FromHexString(gethGenesis.ParentBeaconBlockRoot));
        }

        bool depositsEnabled = config.PragueTime.HasValue && genesisHeader.Timestamp >= config.PragueTime;
        bool withdrawalRequestsEnabled = config.PragueTime.HasValue && genesisHeader.Timestamp >= config.PragueTime;
        bool consolidationRequestsEnabled = config.PragueTime.HasValue && genesisHeader.Timestamp >= config.PragueTime;
        bool requestsEnabled = depositsEnabled || withdrawalRequestsEnabled || consolidationRequestsEnabled;
        if (requestsEnabled)
        {
            genesisHeader.RequestsHash = Core.ExecutionRequest.ExecutionRequestExtensions.EmptyRequestsHash;
        }

        chainSpec.Genesis = !withdrawalsEnabled
            ? new Block(genesisHeader)
            : new Block(
                genesisHeader,
                Array.Empty<Transaction>(),
                Array.Empty<BlockHeader>(),
                Array.Empty<Withdrawal>());
    }

    private static void LoadAllocations(GethGenesisJson gethGenesis, ChainSpec chainSpec)
    {
        if (gethGenesis.Alloc is null)
        {
            chainSpec.Allocations = new Dictionary<Address, ChainSpecAllocation>();
            return;
        }

        chainSpec.Allocations = new Dictionary<Address, ChainSpecAllocation>();
        foreach (KeyValuePair<string, GethGenesisAllocJson> account in gethGenesis.Alloc)
        {
            string addressKey = account.Key.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? account.Key
                : "0x" + account.Key;
            Address address = new(addressKey);

            UInt256 balance = UInt256.Zero;
            if (!string.IsNullOrEmpty(account.Value.Balance))
            {
                balance = ParseHexOrDecimal(account.Value.Balance);
            }

            UInt256 accountNonce = UInt256.Zero;
            if (!string.IsNullOrEmpty(account.Value.Nonce))
            {
                accountNonce = ParseHexOrDecimal(account.Value.Nonce);
            }

            byte[]? code = null;
            if (!string.IsNullOrEmpty(account.Value.Code))
            {
                code = Bytes.FromHexString(account.Value.Code);
            }

            Dictionary<UInt256, byte[]>? storage = null;
            if (account.Value.Storage is not null)
            {
                storage = account.Value.Storage.ToDictionary(
                    static s => Bytes.FromHexString(s.Key).ToUInt256(),
                    static s => Bytes.FromHexString(s.Value));
            }

            chainSpec.Allocations[address] = new ChainSpecAllocation(balance, accountNonce, code, null, storage);
        }
    }

    private static void LoadTransitions(ChainSpec chainSpec)
    {
        chainSpec.HomesteadBlockNumber = 0;
        chainSpec.TangerineWhistleBlockNumber = chainSpec.Parameters.Eip150Transition;
        chainSpec.SpuriousDragonBlockNumber = chainSpec.Parameters.Eip160Transition;
        chainSpec.ByzantiumBlockNumber = chainSpec.Parameters.Eip140Transition;
        chainSpec.ConstantinopleBlockNumber = chainSpec.Parameters.Eip145Transition;
        chainSpec.ConstantinopleFixBlockNumber = chainSpec.Parameters.Eip1283DisableTransition ?? chainSpec.Parameters.Eip145Transition;
        chainSpec.IstanbulBlockNumber = chainSpec.Parameters.Eip2200Transition;
        chainSpec.BerlinBlockNumber = chainSpec.Parameters.Eip2929Transition;
        chainSpec.LondonBlockNumber = chainSpec.Parameters.Eip1559Transition;
        chainSpec.ShanghaiTimestamp = chainSpec.Parameters.Eip3651TransitionTimestamp;
        chainSpec.CancunTimestamp = chainSpec.Parameters.Eip4844TransitionTimestamp;
        chainSpec.PragueTimestamp = chainSpec.Parameters.Eip7002TransitionTimestamp;
        chainSpec.OsakaTimestamp = chainSpec.Parameters.Eip7594TransitionTimestamp;
        chainSpec.MergeForkIdBlockNumber = chainSpec.Parameters.MergeForkIdTransition;
        chainSpec.TerminalTotalDifficulty = chainSpec.Parameters.TerminalTotalDifficulty;
    }

    private static UInt256 ParseHexOrDecimal(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return UInt256.Zero;
        }

        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return UInt256.Parse(value.AsSpan(2), NumberStyles.HexNumber);
        }

        return UInt256.Parse(value);
    }

    private static ulong ParseHexOrDecimalULong(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.Parse(value.AsSpan(2), NumberStyles.HexNumber);
        }

        return ulong.Parse(value);
    }
}

/// <summary>
/// Minimal engine parameters provider for Geth-style genesis files.
/// Defaults to Ethash engine.
/// </summary>
internal sealed class GethGenesisEngineParametersProvider : IChainSpecParametersProvider
{
    public string SealEngineType => Core.SealEngineType.Ethash;

    public IEnumerable<IChainSpecEngineParameters> AllChainSpecParameters => [];

    public T GetChainSpecParameters<T>() where T : IChainSpecEngineParameters =>
        throw new NotSupportedException($"Geth genesis files do not support engine-specific parameters of type {typeof(T).Name}");
}
