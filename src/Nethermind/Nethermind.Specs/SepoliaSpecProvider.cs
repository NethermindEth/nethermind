// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs;

public class SepoliaSpecProvider : ISpecProvider
{
    public const ulong BeaconChainGenesisTimestampConst = 0x62b07d60;
    public const ulong ShanghaiTimestamp = 0x63fd7d60;
    public const ulong CancunTimestamp = 0x65B97D60;
    public const ulong PragueTimestamp = 0x67C7FD60;
    public const ulong OsakaTimestamp = 0x68edfd60;
    public const ulong BPO1Timestamp = 0x68f6fd60;
    public const ulong BPO2Timestamp = 0x68fffd60;

    private static IReleaseSpec? _prague;

    private static IReleaseSpec Prague => LazyInitializer.EnsureInitialized(ref _prague,
        static () => new Prague { DepositContractAddress = Eip6110Constants.SepoliaDepositContractAddress });

    private SepoliaSpecProvider() { }

    public static readonly FrozenDictionary<string, IReleaseSpec> Forks = new Dictionary<string, IReleaseSpec>(StringComparer.OrdinalIgnoreCase)
    {
        [nameof(London)] = London.Instance,
        [nameof(Shanghai)] = Shanghai.Instance,
        [nameof(Cancun)] = Cancun.Instance,
        [nameof(Prague)] = Prague,
        [nameof(Osaka)] = Osaka.Instance,
        [nameof(BPO1)] = BPO1.Instance,
        [nameof(BPO2)] = BPO2.Instance,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public IReleaseSpec GetSpec(ForkActivation forkActivation) =>
        forkActivation switch
        {
            { Timestamp: null } or { Timestamp: < ShanghaiTimestamp } => London.Instance,
            { Timestamp: < CancunTimestamp } => Shanghai.Instance,
            { Timestamp: < PragueTimestamp } => Cancun.Instance,
            { Timestamp: < OsakaTimestamp } => Prague,
            { Timestamp: < BPO1Timestamp } => Osaka.Instance,
            { Timestamp: < BPO2Timestamp } => BPO1.Instance,
            _ => BPO2.Instance
        };

    public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null)
    {
        if (blockNumber is not null)
            MergeBlockNumber = (ForkActivation)blockNumber;
        if (terminalTotalDifficulty is not null)
            TerminalTotalDifficulty = terminalTotalDifficulty;
    }

    public ulong NetworkId => BlockchainIds.Sepolia;
    public ulong ChainId => NetworkId;
    public string SealEngine => SealEngineType.Clique;
    public long? DaoBlockNumber => null;
    public ulong? BeaconChainGenesisTimestamp => BeaconChainGenesisTimestampConst;
    public ForkActivation? MergeBlockNumber { get; private set; } = null;
    public ulong TimestampFork => ISpecProvider.TimestampForkNever;
    public UInt256? TerminalTotalDifficulty { get; private set; } = 17000000000000000;
    public IReleaseSpec GenesisSpec => London.Instance;
    public ForkActivation[] TransitionActivations { get; } =
    [
        (ForkActivation)1735371,
        (1735371, ShanghaiTimestamp),
        (1735371, CancunTimestamp),
        (1735371, PragueTimestamp),
        (1735371, OsakaTimestamp),
        (1735371, BPO1Timestamp),
        (1735371, BPO2Timestamp),
    ];

    public static SepoliaSpecProvider Instance { get; } = new();
}
