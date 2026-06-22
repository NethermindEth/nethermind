// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle.Json;
using Nethermind.Specs.Forks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

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
        chainSpec.EngineChainSpecParametersProvider = new GethGenesisEngineParametersProvider(gethGenesis.Config);
        chainSpec.SealEngineType = chainSpec.EngineChainSpecParametersProvider.SealEngineType;
    }

    private void LoadParameters(GethGenesisJson gethGenesis, ChainSpec chainSpec)
    {
        GethGenesisConfigJson config = gethGenesis.Config;

        Dictionary<ulong, OrderedBlobScheduleSettings> blobSchedulesByTimestamp = [];
        IReadOnlyDictionary<string, ulong>? timestamps = ((IHasNamedForks)config).NamedForkTimestamps;
        if (config.BlobSchedule is not null && timestamps is not null)
        {
            foreach ((string forkName, GethBlobScheduleEntry blobSettings) in config.BlobSchedule)
            {
                if (!_blobScheduleForks.TryGetValue(forkName, out BlobScheduleFork fork)) continue;
                if (!timestamps.TryGetValue(forkName, out ulong timestamp)) continue;

                BlobScheduleSettings settings = new()
                {
                    Timestamp = timestamp,
                    Target = blobSettings.Target,
                    Max = blobSettings.Max,
                    BaseFeeUpdateFraction = blobSettings.BaseFeeUpdateFraction
                };

                if (!blobSchedulesByTimestamp.TryGetValue(timestamp, out OrderedBlobScheduleSettings existing)
                    || fork.Order > existing.ForkOrder)
                {
                    blobSchedulesByTimestamp[timestamp] = new OrderedBlobScheduleSettings(fork.Order, settings);
                }
            }
        }

        SortedSet<BlobScheduleSettings> blobSchedule = [];
        foreach (KeyValuePair<ulong, OrderedBlobScheduleSettings> schedule in blobSchedulesByTimestamp)
        {
            blobSchedule.Add(schedule.Value.Settings);
        }

        chainSpec.Parameters = new ChainParameters
        {
            GasLimitBoundDivisor = 0x400,
            MaximumExtraDataSize = 32,
            MinGasLimit = 5000,
            MinHistoryRetentionEpochs = 82125,
            MinBalRetentionEpochs = 3533,

            Eip7Transition = config.HomesteadBlock ?? 0,

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

            ValidateChainIdTransition = config.Eip155Block ?? config.SpuriousDragonBlock,

            Eip214Transition = config.ByzantiumBlock,
            Eip658Transition = config.ByzantiumBlock,

            Eip145Transition = config.ConstantinopleBlock,

            ValidateReceiptsTransition = config.ByzantiumBlock,
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

            // Post-merge per-EIP timestamp fan-out is driven off `HardforkLabels.All` below; only
            // the side-effects that don't fit the bulk-label pattern stay here as conditional gates.
            Eip4788ContractAddress = config.CancunTime is null ? null : Eip4788Constants.BeaconRootsAddress,
            Eip2935ContractAddress = config.PragueTime is null ? null : Eip2935Constants.BlockHashHistoryAddress,
            DepositContractAddress = config.PragueTime is null ? null : config.DepositContractAddress ?? Address.Zero,
            Eip7002ContractAddress = config.PragueTime is null ? null : Eip7002Constants.WithdrawalRequestPredeployAddress,
            Eip7251ContractAddress = config.PragueTime is null ? null : Eip7251Constants.ConsolidationRequestPredeployAddress,
            Eip7934MaxRlpBlockSize = Eip7934Constants.DefaultMaxRlpBlockSize,

            BlobSchedule = blobSchedule
        };

        // Fan out Shanghai/Cancun/Prague/Osaka/Amsterdam timestamps via the shared HardforkLabels
        // table — same source of truth as the Parity loader, driven by Forks/*.cs.
        HardforkLabels.ExpandAll(chainSpec.Parameters, config);
    }

    private readonly record struct BlobScheduleFork(int Order);

    private readonly record struct OrderedBlobScheduleSettings(int ForkOrder, BlobScheduleSettings Settings);

    private static readonly Dictionary<string, BlobScheduleFork> _blobScheduleForks =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(Cancun)] = new(0),
            [nameof(Prague)] = new(1),
            [nameof(Osaka)] = new(2),
            [nameof(BPO1)] = new(3),
            [nameof(BPO2)] = new(4),
            [nameof(Amsterdam)] = new(5),
            [nameof(BPO3)] = new(6),
            [nameof(BPO4)] = new(7),
            [nameof(BPO5)] = new(8),
        };

    private static void LoadGenesis(GethGenesisJson gethGenesisJson, ChainSpec chainSpec)
    {
        ulong nonce = gethGenesisJson.Nonce;
        Hash256 mixHash = gethGenesisJson.MixHash ?? Keccak.Zero;
        ulong timestamp = gethGenesisJson.Timestamp ?? 0;
        UInt256 difficulty = gethGenesisJson.Difficulty;
        byte[] extraData = gethGenesisJson.ExtraData ?? [];
        ulong gasLimit = gethGenesisJson.GasLimit ?? 0;
        Address beneficiary = gethGenesisJson.Coinbase ?? Address.Zero;
        UInt256 baseFee = gethGenesisJson.Config.LondonBlock switch
        {
            null => gethGenesisJson.BaseFeePerGas ?? UInt256.Zero,
            0 => gethGenesisJson.BaseFeePerGas ?? Eip1559Constants.DefaultForkBaseFee,
            _ => UInt256.Zero,
        };

        BlockHeader genesisHeader = new(
            Keccak.Zero,
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
            StateRoot = Keccak.EmptyTreeHash,
            TxRoot = Keccak.EmptyTreeHash,
            BaseFeePerGas = baseFee
        };

        static bool IsForkActive(ulong? forkTime, ulong timestamp) => forkTime <= timestamp;

        GethGenesisConfigJson config = gethGenesisJson.Config;
        bool isShanghaiActive = IsForkActive(config.ShanghaiTime, genesisHeader.Timestamp);
        bool isCancunActive = IsForkActive(config.CancunTime, genesisHeader.Timestamp);
        bool isPragueActive = IsForkActive(config.PragueTime, genesisHeader.Timestamp);
        bool isAmsterdamActive = IsForkActive(config.AmsterdamTime, genesisHeader.Timestamp);

        if (isShanghaiActive)
        {
            genesisHeader.WithdrawalsRoot = Keccak.EmptyTreeHash;
        }

        if (isPragueActive)
        {
            genesisHeader.RequestsHash = ExecutionRequestExtensions.EmptyRequestsHash;
        }

        if (isCancunActive)
        {
            genesisHeader.BlobGasUsed = gethGenesisJson.BlobGasUsed ?? 0;
            genesisHeader.ExcessBlobGas = gethGenesisJson.ExcessBlobGas ?? 0;
            genesisHeader.ParentBeaconBlockRoot = gethGenesisJson.ParentBeaconBlockRoot ?? Keccak.Zero;
        }

        if (isAmsterdamActive)
        {
            genesisHeader.BlockAccessListHash = Keccak.OfAnEmptySequenceRlp;
            genesisHeader.SlotNumber = gethGenesisJson.SlotNumber ?? 0;
        }

        chainSpec.Bootnodes = [];
        chainSpec.Genesis = isAmsterdamActive
            ? new Block(genesisHeader, [], [], [], new())
            : isShanghaiActive
                ? new Block(genesisHeader, [], [], [])
                : new Block(genesisHeader);
    }

    private static void LoadAllocations(GethGenesisJson gethGenesis, ChainSpec chainSpec)
    {
        chainSpec.Allocations = [];

        if (gethGenesis.Alloc is not null)
        {
            foreach (KeyValuePair<Address, GethGenesisAllocJson> account in gethGenesis.Alloc)
            {
                chainSpec.Allocations[account.Key] = new ChainSpecAllocation(account.Value.Balance ?? 0,
                    account.Value.Nonce ?? 0, account.Value.Code, null, account.Value.Storage);
            }
        }
    }

    private static void LoadTransitions(ChainSpec chainSpec)
    {
        chainSpec.HomesteadBlockNumber = chainSpec.Parameters.Eip7Transition;
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
        chainSpec.AmsterdamTimestamp = chainSpec.Parameters.Eip7928TransitionTimestamp;
        chainSpec.MergeForkIdBlockNumber = chainSpec.Parameters.MergeForkIdTransition;
        chainSpec.TerminalTotalDifficulty = chainSpec.Parameters.TerminalTotalDifficulty;

        if (chainSpec.EngineChainSpecParametersProvider is not null)
        {
            foreach (IChainSpecEngineParameters chainSpecEngineParameters in chainSpec.EngineChainSpecParametersProvider.AllChainSpecParameters)
            {
                chainSpecEngineParameters.ApplyToChainSpec(chainSpec);
            }
        }
    }
}

/// <summary>
/// Ethash engine parameters provider for Geth-style genesis files.
/// </summary>
internal sealed class GethGenesisEngineParametersProvider(GethGenesisConfigJson config) : IChainSpecParametersProvider
{
    private readonly GethEthashChainSpecEngineParameters _engineParameters =
        new(config ?? throw new ArgumentNullException(nameof(config)));

    public string SealEngineType => Core.SealEngineType.Ethash;

    public IEnumerable<IChainSpecEngineParameters> AllChainSpecParameters => [_engineParameters];

    public T GetChainSpecParameters<T>() where T : IChainSpecEngineParameters
    {
        if (_engineParameters is T typedParameters)
        {
            return typedParameters;
        }

        T target = Activator.CreateInstance<T>();

        if (target.EngineName != Core.SealEngineType.Ethash)
        {
            throw new NotSupportedException($"Geth genesis files do not support engine-specific parameters of type {typeof(T).Name}");
        }

        CopyPublicWritableProperties(_engineParameters, target);
        return target;
    }

    private static void CopyPublicWritableProperties(object source, object target)
    {
        foreach (PropertyInfo sourceProperty in source.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (sourceProperty.CanRead)
            {
                PropertyInfo? targetProperty = target.GetType().GetProperty(sourceProperty.Name, BindingFlags.Public | BindingFlags.Instance);
                if (targetProperty is not null && targetProperty.CanWrite)
                {
                    object? value = sourceProperty.GetValue(source);
                    if (value is not null && targetProperty.PropertyType.IsInstanceOfType(value))
                    {
                        targetProperty.SetValue(target, value);
                    }
                }
            }
        }
    }

    private sealed class GethEthashChainSpecEngineParameters(GethGenesisConfigJson config) : IChainSpecEngineParameters
    {
        private static readonly UInt256 FiveEth = new(5_000_000_000_000_000_000ul);
        private static readonly UInt256 ThreeEth = new(3_000_000_000_000_000_000ul);
        private static readonly UInt256 TwoEth = new(2_000_000_000_000_000_000ul);

        public string? EngineName => SealEngineType;
        public string? SealEngineType => Core.SealEngineType.Ethash;

        public ulong HomesteadTransition { get; } = config.HomesteadBlock ?? 0;
        public ulong? DaoHardforkTransition { get; } = config.DaoForkSupport == false ? null : config.DaoForkBlock;
        public Address? DaoHardforkBeneficiary { get; }
        public Address[] DaoHardforkAccounts { get; } = [];
        public ulong? Eip100bTransition { get; } = config.ByzantiumBlock;
        public ulong? FixedDifficulty { get; }
        public ulong DifficultyBoundDivisor => 0x0800;
        public long DurationLimit => 13;
        public UInt256 MinimumDifficulty => UInt256.Zero;
        public SortedDictionary<ulong, UInt256>? BlockReward { get; } = BuildBlockRewardSchedule(config);
        public IDictionary<ulong, ulong>? DifficultyBombDelays { get; } = BuildDifficultyBombDelays(config);

        public void AddTransitions(SortedSet<ulong> blockNumbers, SortedSet<ulong> timestamps)
        {
            if (DifficultyBombDelays is not null)
            {
                foreach ((ulong blockNumber, _) in DifficultyBombDelays)
                {
                    blockNumbers.Add(blockNumber);
                }
            }

            if (BlockReward is not null)
            {
                foreach ((ulong blockNumber, _) in BlockReward)
                {
                    blockNumbers.Add(blockNumber);
                }
            }

            blockNumbers.Add(HomesteadTransition);
            if (DaoHardforkTransition is not null)
            {
                blockNumbers.Add(DaoHardforkTransition.Value);
            }

            if (Eip100bTransition is not null)
            {
                blockNumbers.Add(Eip100bTransition.Value);
            }
        }

        public void ApplyToReleaseSpec(ReleaseSpec spec, ulong startBlock, ulong? startTimestamp)
        {
            if (BlockReward is not null)
            {
                foreach ((ulong blockNumber, UInt256 blockReward) in BlockReward)
                {
                    if (blockNumber <= startBlock)
                    {
                        spec.BlockReward = blockReward;
                    }
                }
            }

            if (DifficultyBombDelays is not null)
            {
                foreach ((ulong blockNumber, ulong bombDelay) in DifficultyBombDelays)
                {
                    if (blockNumber <= startBlock)
                    {
                        spec.DifficultyBombDelay += bombDelay;
                    }
                }
            }

            spec.IsEip2Enabled = HomesteadTransition <= startBlock;
            spec.IsEip7Enabled = HomesteadTransition <= startBlock;
            spec.IsEip100Enabled = (Eip100bTransition ?? 0) <= startBlock;
            spec.DifficultyBoundDivisor = DifficultyBoundDivisor;
            spec.FixedDifficulty = FixedDifficulty;
        }

        public void ApplyToChainSpec(ChainSpec chainSpec)
        {
            chainSpec.HomesteadBlockNumber = HomesteadTransition;
            chainSpec.DaoForkBlockNumber = DaoHardforkTransition;
            chainSpec.MuirGlacierNumber = config.MuirGlacierBlock;
            chainSpec.ArrowGlacierBlockNumber = config.ArrowGlacierBlock;
            chainSpec.GrayGlacierBlockNumber = config.GrayGlacierBlock;
        }

        private static SortedDictionary<ulong, UInt256> BuildBlockRewardSchedule(GethGenesisConfigJson config)
        {
            SortedDictionary<ulong, UInt256> blockReward = [];
            ulong? constantinopleTransition = GetConstantinopleTransition(config);

            blockReward[0] = constantinopleTransition == 0 ? TwoEth
                : config.ByzantiumBlock == 0 ? ThreeEth
                : FiveEth;

            if (config.ByzantiumBlock is > 0)
            {
                blockReward[config.ByzantiumBlock.Value] = ThreeEth;
            }

            if (constantinopleTransition is > 0)
            {
                blockReward[constantinopleTransition.Value] = TwoEth;
            }

            return blockReward;
        }

        private static SortedDictionary<ulong, ulong>? BuildDifficultyBombDelays(GethGenesisConfigJson config)
        {
            if (config.TerminalTotalDifficulty is not null && config.TerminalTotalDifficulty.Value == UInt256.Zero)
            {
                return null;
            }

            SortedDictionary<ulong, ulong> bombDelays = [];
            AddBombDelay(bombDelays, config.ByzantiumBlock, 3_000_000);
            AddBombDelay(bombDelays, GetConstantinopleTransition(config), 2_000_000);
            AddBombDelay(bombDelays, config.MuirGlacierBlock, 4_000_000);
            AddBombDelay(bombDelays, config.ArrowGlacierBlock, 700_000);
            AddBombDelay(bombDelays, config.GrayGlacierBlock, 1_000_000);

            return bombDelays.Count == 0 ? null : bombDelays;
        }

        private static void AddBombDelay(SortedDictionary<ulong, ulong> bombDelays, ulong? transition, ulong delay)
        {
            if (transition is not null)
            {
                bombDelays[transition.Value] = !bombDelays.TryGetValue(transition.Value, out ulong existingDelay)
                    ? delay
                    : existingDelay + delay;
            }
        }

        private static ulong? GetConstantinopleTransition(GethGenesisConfigJson config)
        {
            if (config.ConstantinopleBlock is null) return config.PetersburgBlock;
            if (config.PetersburgBlock is null) return config.ConstantinopleBlock;
            return Math.Min(config.ConstantinopleBlock.Value, config.PetersburgBlock.Value);
        }
    }
}
