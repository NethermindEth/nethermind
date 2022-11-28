// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs.Test
{
    public class CustomSpecProvider : ISpecProvider
    {
        private ForkActivation? _theMergeBlock = null;
        private (ForkActivation forkActivation, IReleaseSpec Release)[] _transitions;

        public ulong ChainId { get; }
        public ForkActivation[] TransitionActivations { get; }

        public CustomSpecProvider(params (ForkActivation forkActivation, IReleaseSpec Release)[] transitions) : this(0, transitions)
        {
        }

        public CustomSpecProvider(ulong chainId, params (ForkActivation forkActivation, IReleaseSpec Release)[] transitions)
        {
            ChainId = chainId;

            if (transitions.Length == 0)
            {
                throw new ArgumentException($"There must be at least one release specified when instantiating {nameof(CustomSpecProvider)}", $"{nameof(transitions)}");
            }

            _transitions = transitions.OrderBy(r => r.forkActivation).ToArray();
            TransitionActivations = _transitions.Select(t => t.forkActivation).ToArray();

            if (transitions[0].forkActivation.BlockNumber != 0L)
            {
                throw new ArgumentException($"First release specified when instantiating {nameof(CustomSpecProvider)} should be at genesis block (0)", $"{nameof(transitions)}");
            }
        }

        public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null)
        {
            if (blockNumber is not null)
                _theMergeBlock = (ForkActivation)blockNumber;
            if (terminalTotalDifficulty is not null)
                TerminalTotalDifficulty = terminalTotalDifficulty;
        }

        public ForkActivation? MergeBlockNumber => _theMergeBlock;
        public UInt256? TerminalTotalDifficulty { get; set; }

#pragma warning disable CS8602
#pragma warning disable CS8603
        public IReleaseSpec GenesisSpec => _transitions?.Length == 0 ? null : _transitions[0].Release;
#pragma warning restore CS8603
#pragma warning restore CS8602

        public IReleaseSpec GetSpec(ForkActivation forkActivation) =>
            _transitions.TryGetSearchedItem(forkActivation,
                CompareTransitionOnBlock,
                out (ForkActivation, IReleaseSpec Release) transition)
                ? transition.Release
                : GenesisSpec;

        private static int CompareTransitionOnBlock(ForkActivation forkActivation, (ForkActivation activation, IReleaseSpec Release) transition) =>
            forkActivation.CompareTo(transition.activation);

        public long? DaoBlockNumber
        {
            get
            {
                (ForkActivation forkActivation, IReleaseSpec daoRelease) = _transitions.SingleOrDefault(t => t.Release == Dao.Instance);
                return daoRelease is not null ? forkActivation.BlockNumber : null;
            }
        }

    }
}
