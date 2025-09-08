// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Core.Test
{
    public class FixedForkActivationChainHeadSpecProvider(
        ISpecProvider specProvider,
        long fixedBlock = 10_000_000,
        ulong? timestamp = null)
        : IChainHeadSpecProvider
    {
        public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null)
        {
            specProvider.UpdateMergeTransitionInfo(blockNumber, terminalTotalDifficulty);
        }

        public ForkActivation? MergeBlockNumber => specProvider.MergeBlockNumber;
        public ulong TimestampFork => specProvider.TimestampFork;
        public UInt256? TerminalTotalDifficulty => specProvider.TerminalTotalDifficulty;

        public IReleaseSpec GenesisSpec => specProvider.GenesisSpec;

        IReleaseSpec ISpecProvider.GetSpecInternal(ForkActivation forkActivation) => specProvider.GetSpec(forkActivation);

        public long? DaoBlockNumber => specProvider.DaoBlockNumber;

        public ulong? BeaconChainGenesisTimestamp => specProvider.BeaconChainGenesisTimestamp;

        public ulong NetworkId => specProvider.NetworkId;
        public ulong ChainId => specProvider.ChainId;

        public ForkActivation[] TransitionActivations => specProvider.TransitionActivations;

        public IReleaseSpec GetCurrentHeadSpec() => specProvider.GetSpec((fixedBlock, timestamp));
    }
}
