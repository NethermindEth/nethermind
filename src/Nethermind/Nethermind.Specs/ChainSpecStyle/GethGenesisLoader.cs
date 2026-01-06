// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle.Json;
using System;
using System.Collections.Generic;
using System.IO;

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

    private ChainSpec ConvertToChainSpec(GethGenesisJson gethGenesisJson)
    {
        ArgumentNullException.ThrowIfNull(gethGenesisJson);
        ArgumentNullException.ThrowIfNull(gethGenesisJson.Config);

        ChainSpec chainSpec = new()
        {
            ChainId = gethGenesisJson.Config.ChainId,
            NetworkId = gethGenesisJson.Config.ChainId
        };

        LoadGenesis(gethGenesisJson, chainSpec);
        LoadEngine(gethGenesisJson, chainSpec);
        LoadAllocations(gethGenesisJson, chainSpec);
        LoadParameters(gethGenesisJson, chainSpec);
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

        SortedSet<BlobScheduleSettings> blobSchedule = [];
        if (config.BlobSchedule is not null)
        {
            foreach ((string forkName, GethBlobScheduleEntry blobSettings) in config.BlobSchedule)
            {
                ulong? timestamp = GetHardforkTimestamp(config, forkName);

                if (timestamp is null)
                {
                    continue;
                }

                blobSchedule.Add(new BlobScheduleSettings
                {
                    Timestamp = timestamp.Value,
                    Target = blobSettings.Target,
                    Max = blobSettings.Max,
                    BaseFeeUpdateFraction = blobSettings.BaseFeeUpdateFraction
                });
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
            Eip3607Transition = config.LondonBlock,

            MergeForkIdTransition = config.MergeNetsplitBlock,
            TerminalTotalDifficulty = config.TerminalTotalDifficulty,

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

            Eip7594TransitionTimestamp = config.OsakaTime,
            Eip7823TransitionTimestamp = config.OsakaTime,
            Eip7825TransitionTimestamp = config.OsakaTime,
            Eip7883TransitionTimestamp = config.OsakaTime,
            Eip7918TransitionTimestamp = config.OsakaTime,
            Eip7934TransitionTimestamp = config.OsakaTime,
            Eip7934MaxRlpBlockSize = Eip7934Constants.DefaultMaxRlpBlockSize,
            Eip7939TransitionTimestamp = config.OsakaTime,
            Eip7951TransitionTimestamp = config.OsakaTime,

            //EipXXXXTransitionTimestamp = config.AmsterdamTime,

            DepositContractAddress = config.DepositContractAddress,
            // Standard EIP contract addresses
            Eip4788ContractAddress = Eip4788Constants.BeaconRootsAddress,
            Eip2935ContractAddress = Eip2935Constants.BlockHashHistoryAddress,
            Eip7002ContractAddress = Eip7002Constants.WithdrawalRequestPredeployAddress,
            Eip7251ContractAddress = Eip7251Constants.ConsolidationRequestPredeployAddress,

            BlobSchedule = blobSchedule
        };
    }

    private static ulong? GetHardforkTimestamp(GethGenesisConfigJson config, string hardforkName)
    {
        return hardforkName.ToLowerInvariant() switch
        {
            "cancun" => config.CancunTime,
            "prague" => config.PragueTime,
            "osaka" => config.OsakaTime,
            "bpo1" => config.Bpo1Time,
            "bpo2" => config.Bpo2Time,
            "bpo3" => config.Bpo3Time,
            "bpo4" => config.Bpo4Time,
            "bpo5" => config.Bpo5Time,
            _ => 0
        };
    }

    private static void LoadGenesis(GethGenesisJson gethGenesisJson, ChainSpec chainSpec)
    {
        UInt256 nonce = gethGenesisJson.Nonce;
        Hash256 mixHash = gethGenesisJson.MixHash ?? Keccak.Zero;
        ulong timestamp = gethGenesisJson.Timestamp ?? 0;
        UInt256 difficulty = gethGenesisJson.Difficulty;
        byte[] extraData = gethGenesisJson.ExtraData ?? [];
        UInt256 gasLimit = gethGenesisJson.GasLimit ?? 0;
        Address beneficiary = gethGenesisJson.Coinbase ?? Address.Zero;
        UInt256 baseFee = gethGenesisJson.BaseFeePerGas ?? UInt256.Zero;
        if (gethGenesisJson.Config.LondonBlock is not null)
            baseFee = gethGenesisJson.Config.LondonBlock == 0
                ? (gethGenesisJson.BaseFeePerGas ?? Eip1559Constants.DefaultForkBaseFee)
                : UInt256.Zero;

        BlockHeader genesisHeader = new(
            Keccak.Zero,
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
            StateRoot = Keccak.EmptyTreeHash,
            TxRoot = Keccak.EmptyTreeHash,
            BaseFeePerGas = baseFee
        };

        bool isEip4844Enabled = gethGenesisJson.Config.CancunTime is not null && genesisHeader.Timestamp >= gethGenesisJson.Config.CancunTime;
        bool withdrawalsEnabled = gethGenesisJson.Config.ShanghaiTime is not null && genesisHeader.Timestamp >= gethGenesisJson.Config.ShanghaiTime;
        bool depositsEnabled = gethGenesisJson.Config.PragueTime is not null && genesisHeader.Timestamp >= gethGenesisJson.Config.PragueTime;
        bool withdrawalRequestsEnabled = gethGenesisJson.Config.PragueTime is not null && genesisHeader.Timestamp >= gethGenesisJson.Config.PragueTime;
        bool consolidationRequestsEnabled = gethGenesisJson.Config.PragueTime is not null && genesisHeader.Timestamp >= gethGenesisJson.Config.PragueTime;

        if (withdrawalsEnabled)
            genesisHeader.WithdrawalsRoot = Keccak.EmptyTreeHash;

        var requestsEnabled = depositsEnabled || withdrawalRequestsEnabled || consolidationRequestsEnabled;
        if (requestsEnabled)
            genesisHeader.RequestsHash = ExecutionRequestExtensions.EmptyRequestsHash;

        if (isEip4844Enabled)
        {
            genesisHeader.BlobGasUsed = gethGenesisJson.BlobGasUsed;
            genesisHeader.ExcessBlobGas = gethGenesisJson.ExcessBlobGas;
        }

        bool isEip4788Enabled = gethGenesisJson.Config.CancunTime is not null && genesisHeader.Timestamp >= gethGenesisJson.Config.CancunTime;
        if (isEip4788Enabled)
        {
            genesisHeader.ParentBeaconBlockRoot = Keccak.Zero;
        }

        if (requestsEnabled)
        {
            genesisHeader.ReceiptsRoot = Keccak.EmptyTreeHash;
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
            chainSpec.Allocations = [];
            return;
        }

        chainSpec.Allocations = [];

        foreach (KeyValuePair<Address, GethGenesisAllocJson> account in gethGenesis.Alloc)
        {
            chainSpec.Allocations[account.Key] = new ChainSpecAllocation(account.Value.Balance ?? 0, account.Value.Nonce ?? 0, account.Value.Code, null, account.Value.Storage);
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
