// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
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

            Eip3651TransitionTimestamp = config.ShanghaiTime,
            Eip3855TransitionTimestamp = config.ShanghaiTime,
            Eip3860TransitionTimestamp = config.ShanghaiTime,
            Eip4895TransitionTimestamp = config.ShanghaiTime,

            Eip1153TransitionTimestamp = config.CancunTime,
            Eip4844TransitionTimestamp = config.CancunTime,
            Eip4788TransitionTimestamp = config.CancunTime,
            Eip4788ContractAddress = config.CancunTime is null ? null : Eip4788Constants.BeaconRootsAddress,
            Eip5656TransitionTimestamp = config.CancunTime,
            Eip6780TransitionTimestamp = config.CancunTime,

            Eip2537TransitionTimestamp = config.PragueTime,
            Eip2935TransitionTimestamp = config.PragueTime,
            Eip2935ContractAddress = config.PragueTime is null ? null : Eip2935Constants.BlockHashHistoryAddress,

            Eip6110TransitionTimestamp = config.PragueTime,
            DepositContractAddress = config.PragueTime is null ? null : config.DepositContractAddress ?? Eip6110Constants.MainnetDepositContractAddress,

            Eip7002TransitionTimestamp = config.PragueTime,
            Eip7002ContractAddress = config.PragueTime is null ? null : Eip7002Constants.WithdrawalRequestPredeployAddress,

            Eip7251TransitionTimestamp = config.PragueTime,
            Eip7251ContractAddress = config.PragueTime is null ? null : Eip7251Constants.ConsolidationRequestPredeployAddress,

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

            Eip7708TransitionTimestamp = config.AmsterdamTime,
            Eip7778TransitionTimestamp = config.AmsterdamTime,
            Eip7843TransitionTimestamp = config.AmsterdamTime,
            Eip7928TransitionTimestamp = config.AmsterdamTime,
            Eip7954TransitionTimestamp = config.AmsterdamTime,
            Eip8024TransitionTimestamp = config.AmsterdamTime,
            Eip8037TransitionTimestamp = config.AmsterdamTime,

            BlobSchedule = blobSchedule
        };
    }

    private readonly Dictionary<string, Func<GethGenesisConfigJson, ulong?>> _hardforkTimestampGetters =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(Cancun)] = static c => c.CancunTime,
            [nameof(Prague)] = static c => c.PragueTime,
            [nameof(Osaka)] = static c => c.OsakaTime,
            [nameof(BPO1)] = static c => c.Bpo1Time,
            [nameof(BPO2)] = static c => c.Bpo2Time,
            [nameof(BPO3)] = static c => c.Bpo3Time,
            [nameof(BPO4)] = static c => c.Bpo4Time,
            [nameof(BPO5)] = static c => c.Bpo5Time,
        };

    private ulong? GetHardforkTimestamp(GethGenesisConfigJson config, string hardforkName) =>
        _hardforkTimestampGetters.TryGetValue(hardforkName, out Func<GethGenesisConfigJson, ulong?> getter) ? getter(config) : null;

    private static void LoadGenesis(GethGenesisJson gethGenesisJson, ChainSpec chainSpec)
    {
        UInt256 nonce = gethGenesisJson.Nonce;
        Hash256 mixHash = gethGenesisJson.MixHash ?? Keccak.Zero;
        ulong timestamp = gethGenesisJson.Timestamp ?? 0;
        UInt256 difficulty = gethGenesisJson.Difficulty;
        byte[] extraData = gethGenesisJson.ExtraData ?? [];
        UInt256 gasLimit = gethGenesisJson.GasLimit ?? 0;
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
            genesisHeader.SlotNumber = 0;
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

        Type requestedType = typeof(T);
        if (requestedType.Name != "EthashChainSpecEngineParameters")
        {
            throw new NotSupportedException($"Geth genesis files do not support engine-specific parameters of type {requestedType.Name}");
        }

        object target = Activator.CreateInstance(requestedType)
            ?? throw new NotSupportedException($"Could not create engine-specific parameters of type {requestedType.Name}");

        CopyPublicWritableProperties(_engineParameters, target);
        return (T)target;
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
        private readonly long? _arrowGlacierTransition = config.ArrowGlacierBlock;
        private readonly long? _grayGlacierTransition = config.GrayGlacierBlock;
        private readonly long? _muirGlacierTransition = config.MuirGlacierBlock;

        public string? EngineName => SealEngineType;
        public string? SealEngineType => Core.SealEngineType.Ethash;
        public long HomesteadTransition { get; } = config.HomesteadBlock ?? 0;
        public long? DaoHardforkTransition { get; } = config.DaoForkSupport == false ? null : config.DaoForkBlock;
        public Address? DaoHardforkBeneficiary { get; }
        public Address[] DaoHardforkAccounts { get; } = [];
        public long? Eip100bTransition { get; } = config.ByzantiumBlock;
        public long? FixedDifficulty { get; }
        public long DifficultyBoundDivisor => 0x0800;
        public long DurationLimit => 13;
        public UInt256 MinimumDifficulty => UInt256.Zero;
        public SortedDictionary<long, UInt256>? BlockReward { get; } = BuildBlockRewardSchedule(config);
        public IDictionary<long, long>? DifficultyBombDelays { get; } = BuildDifficultyBombDelays(config);

        public void AddTransitions(SortedSet<long> blockNumbers, SortedSet<ulong> timestamps)
        {
            if (DifficultyBombDelays is not null)
            {
                foreach ((long blockNumber, _) in DifficultyBombDelays)
                {
                    blockNumbers.Add(blockNumber);
                }
            }

            if (BlockReward is not null)
            {
                foreach ((long blockNumber, _) in BlockReward)
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

        public void ApplyToReleaseSpec(ReleaseSpec spec, long startBlock, ulong? startTimestamp)
        {
            if (BlockReward is not null)
            {
                foreach ((long blockNumber, UInt256 blockReward) in BlockReward)
                {
                    if (blockNumber <= startBlock)
                    {
                        spec.BlockReward = blockReward;
                    }
                }
            }

            if (DifficultyBombDelays is not null)
            {
                foreach ((long blockNumber, long bombDelay) in DifficultyBombDelays)
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
            chainSpec.MuirGlacierNumber = _muirGlacierTransition;
            chainSpec.ArrowGlacierBlockNumber = _arrowGlacierTransition;
            chainSpec.GrayGlacierBlockNumber = _grayGlacierTransition;
        }

        private static SortedDictionary<long, UInt256> BuildBlockRewardSchedule(GethGenesisConfigJson config)
        {
            SortedDictionary<long, UInt256> blockReward = [];
            long? constantinopleTransition = GetConstantinopleTransition(config);

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

        private static SortedDictionary<long, long>? BuildDifficultyBombDelays(GethGenesisConfigJson config)
        {
            if (config.TerminalTotalDifficulty is not null && config.TerminalTotalDifficulty.Value == UInt256.Zero)
            {
                return null;
            }

            SortedDictionary<long, long> bombDelays = [];
            AddBombDelay(bombDelays, config.ByzantiumBlock, 3_000_000);
            AddBombDelay(bombDelays, GetConstantinopleTransition(config), 2_000_000);
            AddBombDelay(bombDelays, config.MuirGlacierBlock, 4_000_000);
            AddBombDelay(bombDelays, config.ArrowGlacierBlock, 700_000);
            AddBombDelay(bombDelays, config.GrayGlacierBlock, 1_000_000);

            return bombDelays.Count == 0 ? null : bombDelays;
        }

        private static void AddBombDelay(SortedDictionary<long, long> bombDelays, long? transition, long delay)
        {
            if (transition is not null)
            {
                bombDelays[transition.Value] = !bombDelays.TryGetValue(transition.Value, out long existingDelay)
                    ? delay
                    : existingDelay + delay;
            }
        }

        private static long? GetConstantinopleTransition(GethGenesisConfigJson config) =>
            config.ConstantinopleBlock is null ? config.PetersburgBlock
            : config.PetersburgBlock is null ? config.ConstantinopleBlock
            : Math.Min(config.ConstantinopleBlock.Value, config.PetersburgBlock.Value);
    }
}
