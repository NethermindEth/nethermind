// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs;

[System.Obsolete("Goerli testnet has been deprecated. Use SepoliaSpecProvider instead.")]
public class GoerliSpecProvider : ISpecProvider
{
    // Constants preserved for backward compatibility
    public const long IstanbulBlockNumber = 1_561_651;
    public const long BerlinBlockNumber = 4_460_644;
    public const long LondonBlockNumber = 5_062_605;
    public const ulong BeaconChainGenesisTimestampConst = 0x6059f460;
    public const ulong ShanghaiTimestamp = 0x6410f460;
    public const ulong CancunTimestamp = 0x65A77460;

    private GoerliSpecProvider() { }

    // Delegate to SepoliaSpecProvider for all operations
    private readonly ISpecProvider _sepoliaProvider = SepoliaSpecProvider.Instance;

    public IReleaseSpec GetSpec(ForkActivation forkActivation)
    {
        return _sepoliaProvider.GetSpec(forkActivation);
    }

    public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null)
    {
        _sepoliaProvider.UpdateMergeTransitionInfo(blockNumber, terminalTotalDifficulty);
    }

    public ulong NetworkId => BlockchainIds.Sepolia;
    public ulong ChainId => BlockchainIds.Sepolia;
    public string SealEngine => _sepoliaProvider.SealEngine;
    public long? DaoBlockNumber => _sepoliaProvider.DaoBlockNumber;
    public ulong? BeaconChainGenesisTimestamp => _sepoliaProvider.BeaconChainGenesisTimestamp;
    public ForkActivation? MergeBlockNumber => _sepoliaProvider.MergeBlockNumber;
    public ulong TimestampFork => _sepoliaProvider.TimestampFork;
    public UInt256? TerminalTotalDifficulty => _sepoliaProvider.TerminalTotalDifficulty;
    public IReleaseSpec GenesisSpec => _sepoliaProvider.GenesisSpec;
    public ForkActivation[] TransitionActivations => _sepoliaProvider.TransitionActivations;

    public static readonly GoerliSpecProvider Instance = new();
}
