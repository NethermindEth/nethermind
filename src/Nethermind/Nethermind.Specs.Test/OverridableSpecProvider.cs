// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Specs.Test
{
    public class OverridableSpecProvider : ISpecProvider
    {
        public ISpecProvider SpecProvider { get; }
        private readonly Func<IReleaseSpec, ForkActivation, IReleaseSpec> _overrideAction;

        public OverridableSpecProvider(ISpecProvider specProvider, Func<IReleaseSpec, IReleaseSpec> overrideAction)
            : this(specProvider, (spec, _) => overrideAction(spec))
        {
        }

        public OverridableSpecProvider(ISpecProvider specProvider, Func<IReleaseSpec, ForkActivation, IReleaseSpec> overrideAction)
        {
            SpecProvider = specProvider;
            _overrideAction = overrideAction;
            TimestampFork = SpecProvider.TimestampFork;
        }

        public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null)
        {
            SpecProvider.UpdateMergeTransitionInfo(blockNumber, terminalTotalDifficulty);
        }

        public ForkActivation? MergeBlockNumber => SpecProvider.MergeBlockNumber;

        public ulong TimestampFork { get; set; }

        public UInt256? TerminalTotalDifficulty => SpecProvider.TerminalTotalDifficulty;

        public IReleaseSpec GenesisSpec => _overrideAction(SpecProvider.GenesisSpec, new ForkActivation(0));

        public IReleaseSpec GetSpec(ForkActivation forkActivation) => _overrideAction(SpecProvider.GetSpec(forkActivation), forkActivation);

        public long? DaoBlockNumber => SpecProvider.DaoBlockNumber;
        public ulong? BeaconChainGenesisTimestamp => SpecProvider.BeaconChainGenesisTimestamp;
        public ulong NetworkId => SpecProvider.NetworkId;
        public ulong ChainId => SpecProvider.ChainId;
        public string SealEngine => SpecProvider.SealEngine;

        public ForkActivation[] TransitionActivations => SpecProvider.TransitionActivations;
    }
}
