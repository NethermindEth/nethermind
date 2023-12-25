// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using System;
using System.Linq;

namespace Nethermind.Specs.ChainSpecStyle;

public abstract class SpecProviderBase
{
    protected (ForkActivation Activation, IReleaseSpec Spec)[] _blockTransitions;
    private (ForkActivation Activation, IReleaseSpec Spec)[] _timestampTransitions;
    private ForkActivation? _firstTimestampActivation;
    protected readonly ILogger _logger;

    public SpecProviderBase(ILogger logger = null)
    {
        _logger = logger;
    }

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

        _blockTransitions = transitions.TakeWhile(t => t.Activation.Timestamp is null).ToArray();
        _timestampTransitions = transitions.SkipWhile(t => t.Activation.Timestamp is null).ToArray();
        _firstTimestampActivation = _timestampTransitions.Length != 0 ? _timestampTransitions.First().Activation : null;
        GenesisSpec = transitions.First().Spec;
    }

    public ForkActivation[] TransitionActivations { get; protected set; }

    public IReleaseSpec GenesisSpec { get; private set; }

    public IReleaseSpec GetSpec(ForkActivation activation)
    {
        static int CompareTransitionOnActivation(ForkActivation activation, (ForkActivation Activation, IReleaseSpec Spec) transition) =>
           activation.CompareTo(transition.Activation);

        (ForkActivation Activation, IReleaseSpec Spec)[] consideredTransitions = _blockTransitions;

        if (_firstTimestampActivation is not null && activation.Timestamp is not null)
        {
            if (_firstTimestampActivation.Value.Timestamp < activation.Timestamp
                && _firstTimestampActivation.Value.BlockNumber > activation.BlockNumber)
            {
                if (_logger is not null && _logger.IsWarn) _logger.Warn($"Chainspec file is misconfigured! Timestamp transition is configured to happen before the last block transition.");
            }

            if (_firstTimestampActivation.Value.Timestamp <= activation.Timestamp)
                consideredTransitions = _timestampTransitions;
        }

        return consideredTransitions.TryGetSearchedItem(activation,
            CompareTransitionOnActivation,
            out (ForkActivation Activation, IReleaseSpec Spec) transition)
            ? transition.Spec
            : GenesisSpec;
    }
}
