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
        private readonly (ForkActivation Activation, IReleaseSpec Spec)[] _transitions;
        private (ForkActivation Activation, IReleaseSpec Spec)[] _timestampOnlyTransitions;
        private ForkActivation? _firstTimestampActivation;

        public ulong NetworkId { get; }
        public ulong ChainId { get; }

        public ForkActivation[] TransitionActivations { get; }

        public CustomSpecProvider(params (ForkActivation Activation, IReleaseSpec Spec)[] transitions) : this(TestBlockchainIds.NetworkId, TestBlockchainIds.ChainId, transitions)
        {
        }

        public CustomSpecProvider(ulong networkId, ulong chainId, params (ForkActivation Activation, IReleaseSpec Spec)[] transitions)
        {
            NetworkId = networkId;
            ChainId = chainId;

            if (transitions.Length == 0)
            {
                throw new ArgumentException($"There must be at least one release specified when instantiating {nameof(CustomSpecProvider)}", $"{nameof(transitions)}");
            }

            _transitions = transitions.OrderBy(r => r.Activation).ToArray();
            TransitionActivations = _transitions.Select(t => t.Activation).ToArray();
            _firstTimestampActivation = TransitionActivations.FirstOrDefault(t => t.Timestamp is not null);
            _timestampOnlyTransitions = _transitions.SkipWhile(t => t.Activation.Timestamp is null).ToArray();
            _transitions = _transitions.TakeWhile(t => t.Activation.Timestamp is null).ToArray();

            if (transitions[0].Activation.BlockNumber != 0L)
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

        public ulong TimestampFork { get; set; } = ISpecProvider.TimestampForkNever;
        public UInt256? TerminalTotalDifficulty { get; set; }

#pragma warning disable CS8602
#pragma warning disable CS8603
        public IReleaseSpec GenesisSpec => _transitions.Length == 0 ? null : _transitions[0].Spec;
#pragma warning restore CS8603
#pragma warning restore CS8602

        public IReleaseSpec GetSpec(ForkActivation forkActivation)
        {
            (ForkActivation Activation, IReleaseSpec Spec)[] consideredTransitions = _transitions;

            // TODO: Is this actually needed? Can this be tricked with invalid activation check if someone would fake timestamp from the future?
            if (_firstTimestampActivation is not null && forkActivation.Timestamp is not null)
            {
                if (_firstTimestampActivation.Value.Timestamp < forkActivation.Timestamp
                    && _firstTimestampActivation.Value.BlockNumber > forkActivation.BlockNumber)
                {
                    throw new Exception($"Chainspec file is misconfigured! Timestamp transition is configured to happen before the last block transition.");
                }

                if (_firstTimestampActivation.Value.Timestamp <= forkActivation.Timestamp)
                {
                    consideredTransitions = _timestampOnlyTransitions;
                }
            }

            return consideredTransitions.TryGetSearchedItem(forkActivation,
                CompareTransitionOnBlock,
                out (ForkActivation Activation, IReleaseSpec Spec) transition)
                ? transition.Spec
                : GenesisSpec;
        }
           

        private static int CompareTransitionOnBlock(ForkActivation forkActivation, (ForkActivation Activation, IReleaseSpec Spec) transition) =>
            forkActivation.CompareTo(transition.Activation);

        public long? DaoBlockNumber
        {
            get
            {
                (ForkActivation forkActivation, IReleaseSpec daoRelease) = _transitions.SingleOrDefault(t => t.Spec == Dao.Instance);
                return daoRelease is not null ? forkActivation.BlockNumber : null;
            }
        }

    }
}
