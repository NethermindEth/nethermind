// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs;

public class SepoliaSpecProvider : ISpecProvider
{
    private ForkActivation? _theMergeBlock = null;
    private UInt256? _terminalTotalDifficulty = 17000000000000000;

    public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null)
    {
        if (blockNumber is not null)
            _theMergeBlock = (ForkActivation)blockNumber;
        if (terminalTotalDifficulty is not null)
            _terminalTotalDifficulty = terminalTotalDifficulty;
    }

    public ForkActivation? MergeBlockNumber => _theMergeBlock;
    public ulong TimestampFork => ISpecProvider.TimestampForkNever;
    public UInt256? TerminalTotalDifficulty => _terminalTotalDifficulty;
    public IReleaseSpec GenesisSpec => London.Instance;

    public const ulong ShanghaiBlockTimestamp = 1677557088;

    public IReleaseSpec GetSpec(ForkActivation forkActivation) =>
        forkActivation switch
        {
            { Timestamp: null } or { Timestamp: < ShanghaiBlockTimestamp } => London.Instance,
            _ => Shanghai.Instance
        };

    public long? DaoBlockNumber => null;


    public ulong NetworkId => Core.BlockchainIds.Rinkeby;
    public ulong ChainId => NetworkId;

    public ForkActivation[] TransitionActivations { get; } = { (ForkActivation)1735371, new ForkActivation(1735371, 1677557088) };

    private SepoliaSpecProvider() { }

    public static readonly SepoliaSpecProvider Instance = new();
}
