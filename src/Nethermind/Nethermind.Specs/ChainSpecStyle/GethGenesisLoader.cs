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

        // Parse terminal total difficulty if present
        UInt256? terminalTotalDifficulty = null;
        if (!string.IsNullOrEmpty(config.TerminalTotalDifficulty))
        {
            terminalTotalDifficulty = ParseHexOrDecimal(config.TerminalTotalDifficulty);
        }

        // Convert blob schedule from Geth format to Nethermind format
        SortedSet<BlobScheduleSettings> blobSchedule = [];
        if (config.BlobSchedule is not null)
        {
            foreach (KeyValuePair<string, GethBlobScheduleEntry> entry in config.BlobSchedule)
            {
                // First try explicit timestamp, then fall back to hardfork time lookup
                ulong timestamp = entry.Value.Timestamp ?? GetHardforkTimestamp(config, entry.Key);
                if (timestamp > 0)
                {
                    blobSchedule.Add(new BlobScheduleSettings
                    {
                        Timestamp = timestamp,
                        Target = entry.Value.Target,
                        Max = entry.Value.Max,
                        BaseFeeUpdateFraction = entry.Value.BaseFeeUpdateFraction
                    });
                }
            }
        }

        chainSpec.Parameters = new ChainParameters
        {
            GasLimitBoundDivisor = 0x400,
            MaximumExtraDataSize = 32,
            MinGasLimit = 5000,
            MinHistoryRetentionEpochs = 82125,
            // MaxCodeSize (EIP-170) is standard on all networks since Spurious Dragon
            MaxCodeSize = 0x6000,
            MaxCodeSizeTransition = config.Eip158Block ?? config.SpuriousDragonBlock ?? 0,
            Eip150Transition = config.Eip150Block ?? config.TangerineWhistleBlock ?? 0,
            Eip160Transition = config.Eip155Block ?? config.SpuriousDragonBlock ?? 0,
            Eip161abcTransition = config.Eip158Block ?? config.SpuriousDragonBlock ?? 0,
            Eip161dTransition = config.Eip158Block ?? config.SpuriousDragonBlock ?? 0,
            Eip155Transition = config.Eip155Block ?? config.SpuriousDragonBlock ?? 0,
            Eip140Transition = config.ByzantiumBlock,
            Eip211Transition = config.ByzantiumBlock,
            Eip214Transition = config.ByzantiumBlock,
            Eip658Transition = config.ByzantiumBlock,
            Eip145Transition = config.ConstantinopleBlock,
            Eip1014Transition = config.ConstantinopleBlock,
            Eip1052Transition = config.ConstantinopleBlock,
            Eip1283Transition = config.ConstantinopleBlock,
            Eip1283DisableTransition = config.PetersburgBlock,
            Eip152Transition = config.IstanbulBlock,
            Eip1108Transition = config.IstanbulBlock,
            Eip1344Transition = config.IstanbulBlock,
            Eip1884Transition = config.IstanbulBlock,
            Eip2028Transition = config.IstanbulBlock,
            Eip2200Transition = config.IstanbulBlock,
            Eip2565Transition = config.BerlinBlock,
            Eip2929Transition = config.BerlinBlock,
            Eip2930Transition = config.BerlinBlock,
            Eip1559Transition = config.LondonBlock,
            Eip3198Transition = config.LondonBlock,
            Eip3529Transition = config.LondonBlock,
            Eip3541Transition = config.LondonBlock,
            // EIP-3607 (reject txs from senders with deployed code) is default on since London
            Eip3607Transition = config.LondonBlock,
            MergeForkIdTransition = config.MergeNetsplitBlock,
            TerminalTotalDifficulty = terminalTotalDifficulty,
            Eip3651TransitionTimestamp = config.ShanghaiTime,
            Eip3855TransitionTimestamp = config.ShanghaiTime,
            Eip3860TransitionTimestamp = config.ShanghaiTime,
            Eip4895TransitionTimestamp = config.ShanghaiTime,
            Eip1153TransitionTimestamp = config.CancunTime,
            Eip4844TransitionTimestamp = config.CancunTime,
            Eip4788TransitionTimestamp = config.CancunTime,
            Eip5656TransitionTimestamp = config.CancunTime,
            Eip6780TransitionTimestamp = config.CancunTime,
            Eip2537TransitionTimestamp = config.PragueTime,
            Eip2935TransitionTimestamp = config.PragueTime,
            Eip6110TransitionTimestamp = config.PragueTime,
            Eip7002TransitionTimestamp = config.PragueTime,
            Eip7251TransitionTimestamp = config.PragueTime,
            Eip7623TransitionTimestamp = config.PragueTime,
            Eip7702TransitionTimestamp = config.PragueTime,
            // Osaka EIPs
            Eip7594TransitionTimestamp = config.OsakaTime,
            Eip7823TransitionTimestamp = config.OsakaTime,
            Eip7825TransitionTimestamp = config.OsakaTime,
            Eip7883TransitionTimestamp = config.OsakaTime,
            Eip7918TransitionTimestamp = config.OsakaTime,
            Eip7934TransitionTimestamp = config.OsakaTime,
            Eip7934MaxRlpBlockSize = Eip7934Constants.DefaultMaxRlpBlockSize,
            Eip7939TransitionTimestamp = config.OsakaTime,
            Eip7951TransitionTimestamp = config.OsakaTime,
            DepositContractAddress = config.DepositContractAddress,
            // Standard EIP contract addresses
            Eip4788ContractAddress = Eip4788Constants.BeaconRootsAddress,
            Eip2935ContractAddress = Eip2935Constants.BlockHashHistoryAddress,
            Eip7002ContractAddress = Eip7002Constants.WithdrawalRequestPredeployAddress,
            Eip7251ContractAddress = Eip7251Constants.ConsolidationRequestPredeployAddress,
            BlobSchedule = blobSchedule
        };
    }

    private static ulong GetHardforkTimestamp(GethGenesisConfigJson config, string hardforkName)
    {
        return hardforkName.ToLowerInvariant() switch
        {
            "cancun" => config.CancunTime ?? 0,
            "prague" => config.PragueTime ?? 0,
            "osaka" => config.OsakaTime ?? 0,
            _ => 0
        };
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
