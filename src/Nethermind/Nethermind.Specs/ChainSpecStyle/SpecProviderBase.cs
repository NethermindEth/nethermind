// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using System;
using System.Linq;

namespace Nethermind.Specs.ChainSpecStyle;

public abstract class SpecProviderBase(ILogger? logger = null)
{
    protected (ForkActivation Activation, IReleaseSpec Spec)[] _blockTransitions = [];
    private (ForkActivation Activation, IReleaseSpec Spec)[] _timestampTransitions = [];
    private ForkActivation? _firstTimestampActivation;
    protected readonly ILogger _logger = logger ?? LimboTraceLogger.Instance;
    private IReleaseSpec? _genesisSpec;

    /// <exception cref="ArgumentException">
    /// <paramref name="transitions"/> is empty, does not start at genesis block 0, or orders a block-number
    /// transition after a timestamp transition, where it could never activate.
    /// </exception>
    protected void LoadTransitions((ForkActivation Activation, IReleaseSpec Spec)[] transitions)
    {
        if (transitions.Length == 0)
        {
            throw new ArgumentException($"There must be at least one release specified when instantiating {GetType()}", $"{nameof(transitions)}");
        }

        if (transitions.First().Activation.BlockNumber != 0L)
        {
            throw new ArgumentException($"First release specified when instantiating {GetType()} should be at genesis block (0)", $"{nameof(transitions)}");
        }

        _blockTransitions = transitions.TakeWhile(static t => t.Activation.Timestamp is null).ToArray();
        _timestampTransitions = transitions.SkipWhile(static t => t.Activation.Timestamp is null).ToArray();

        // The split above assumes every block-number transition precedes every timestamp transition. One that does
        // not lands in _timestampTransitions, where GetSpec compares it by timestamp against a null Timestamp, so
        // it silently never activates.
        int strayBlockTransition = Array.FindIndex(_timestampTransitions, static t => t.Activation.Timestamp is null);
        if (strayBlockTransition >= 0)
        {
            throw new ArgumentException(
                $"Release transitions passed to {GetType()} put a block-number transition " +
                $"({_timestampTransitions[strayBlockTransition].Activation.BlockNumber}) after a timestamp " +
                $"transition ({_timestampTransitions[0].Activation.Timestamp}). Every block-number transition must " +
                "come first, otherwise it can never activate.",
                nameof(transitions));
        }
        _firstTimestampActivation = _timestampTransitions.Length != 0 ? _timestampTransitions.First().Activation : null;
        _genesisSpec = transitions.First().Spec;
    }

    public ForkActivation[] TransitionActivations { get; protected set; } = [];

    public IReleaseSpec GenesisSpec => _genesisSpec
        ?? throw new InvalidOperationException("Release transitions have not been loaded.");

    public IReleaseSpec GetSpec(ForkActivation activation)
    {
        static int CompareTransitionOnActivation(ForkActivation activation, (ForkActivation Activation, IReleaseSpec Spec) transition) =>
           activation.CompareTo(transition.Activation);

        (ForkActivation Activation, IReleaseSpec Spec)[] consideredTransitions = _blockTransitions;

        // Ordering is validated in LoadTransitions, not here. A per-call check cannot see a file error:
        // ChainSpecBasedSpecProvider derives the block number of every timestamp activation from the chainspec's
        // own largest block transition, so comparing it against the caller's block number only ever says that the
        // node is below that fork - an ordinary syncing node (#13202).
        if (_firstTimestampActivation is not null
            && activation.Timestamp is not null
            && _firstTimestampActivation.Value.Timestamp <= activation.Timestamp)
        {
            consideredTransitions = _timestampTransitions;
        }

        return consideredTransitions.TryGetSearchedItem(activation,
            CompareTransitionOnActivation,
            out (ForkActivation Activation, IReleaseSpec Spec) transition)
            ? transition.Spec
            : GenesisSpec;
    }
}
