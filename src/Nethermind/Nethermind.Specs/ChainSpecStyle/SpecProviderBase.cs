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

        // The split above assumes every block-number transition precedes every timestamp transition. A block-number
        // transition that appears after a timestamp one lands in _timestampTransitions, where GetSpec compares it by
        // timestamp against a null Timestamp, so it silently never activates. That is a real chainspec ordering
        // error, and unlike the per-call check this replaces, it is decidable here.
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

        // There used to be a "Chainspec file is misconfigured!" warning here, comparing the first timestamp
        // transition against the *requested* activation. It could not detect a misconfigured chainspec, because
        // ChainSpecBasedSpecProvider derives the block number of every timestamp activation from the chainspec's
        // largest block transition rather than reading it from the file - so the two operands were the chainspec's
        // own largest block transition and the caller's block number. It fired precisely when a caller asked for
        // "this block, at the current wall-clock time" while the node was still below that fork, which is an
        // ordinary syncing node and not a configuration error. See #13202: on a syncing mainnet node it was emitted
        // once per eth_estimateGas call (87,517 lines against 87,481 calls), and on a gnosis node replaying blocks
        // it was 99.2% of all log output. Ordering is validated once in LoadTransitions instead, where the
        // chainspec is actually available.
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
