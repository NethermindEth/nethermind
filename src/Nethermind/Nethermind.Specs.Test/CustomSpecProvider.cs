// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs.Test;

public class CustomSpecProvider : SpecProviderBase, ISpecProvider
{
    private ForkActivation? _theMergeBlock = null;

    public ulong NetworkId { get; }
    public ulong ChainId { get; }

    public CustomSpecProvider(params (ForkActivation Activation, IReleaseSpec Spec)[] transitions) : this(TestBlockchainIds.NetworkId, TestBlockchainIds.ChainId, transitions)
    {
    }

    public CustomSpecProvider(ulong networkId, ulong chainId, params (ForkActivation Activation, IReleaseSpec Spec)[] transitions)
    {
        NetworkId = networkId;
        ChainId = chainId;

        (ForkActivation Activation, IReleaseSpec Spec)[] orderedTransitions = transitions.OrderBy(r => r.Activation).ToArray();

        LoadTransitions(orderedTransitions);

        TransitionActivations = orderedTransitions.Select(t => t.Activation).ToArray();
    }

    public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null)
    {
        if (blockNumber is not null)
            _theMergeBlock = (ForkActivation)blockNumber;
        if (terminalTotalDifficulty is not null)
            TerminalTotalDifficulty = terminalTotalDifficulty;
    }

    public ForkActivation? MergeBlockNumber => _theMergeBlock;

    public ulong TimestampFork { get; set; } = ISpecProvider.TimestampForkNever;
    public UInt256? TerminalTotalDifficulty { get; set; }

    public long? DaoBlockNumber
    {
        get
        {
            (ForkActivation forkActivation, IReleaseSpec? daoRelease) = _blockTransitions.SingleOrDefault(t => t.Spec == Dao.Instance);
            return daoRelease is not null ? forkActivation.BlockNumber : null;
        }
    }

}

