// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
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
        public ForkActivation[] TransitionBlocks { get; }

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
            TransitionBlocks = _transitions.Select(t => t.forkActivation).ToArray();

            if (transitions[0].forkActivation.BlockNumber != 0L)
            {
                throw new ArgumentException($"First release specified when instantiating {nameof(CustomSpecProvider)} should be at genesis block (0)", $"{nameof(transitions)}");
            }
        }

        public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null)
        {
            if (blockNumber is not null)
                _theMergeBlock = blockNumber;
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

        public IReleaseSpec GetSpec(ForkActivation forkActivation)
        {
            IReleaseSpec spec = _transitions[0].Release;
            for (int i = 1; i < _transitions.Length; i++)
            {
                if (forkActivation >= _transitions[i].forkActivation)
                {
                    spec = _transitions[i].Release;
                }
                else
                {
                    break;
                }
            }

            return spec;
        }

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
