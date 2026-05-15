// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.ChainSpecStyle;

/// <summary>
/// Per-EIP transition surface used by <see cref="HardforkLabels"/>. Implemented by both
/// <see cref="Nethermind.Specs.ChainSpecStyle.Json.ChainSpecParamsJson"/> (wire format) and
/// <see cref="ChainParameters"/> (post-processed model). Property names mirror each other across
/// the two implementers and are enforced by
/// <c>ChainParametersTests.ChainParameters_should_have_same_properties_as_chainSpecParamsJson</c>.
/// </summary>
/// <remarks>
/// Adding a new EIP transition to a fork: define the new <c>EipNNNTransition[Timestamp]</c>
/// property on both <c>ChainSpecParamsJson</c> and <c>ChainParameters</c>, list it here, and
/// reference it from the corresponding <c>Forks/*.cs</c> <c>Apply</c> method — the source
/// generator picks it up automatically from there.
/// </remarks>
public interface IEipTransitionFields
{
    // Pre-Shanghai — block-activated.
    long? Eip7Transition { get; set; }
    long? Eip140Transition { get; set; }
    long? Eip145Transition { get; set; }
    long? Eip150Transition { get; set; }
    long? Eip152Transition { get; set; }
    long? Eip155Transition { get; set; }
    long? Eip160Transition { get; set; }
    long? Eip161abcTransition { get; set; }
    long? Eip161dTransition { get; set; }
    long? Eip211Transition { get; set; }
    long? Eip214Transition { get; set; }
    long? Eip658Transition { get; set; }
    long? Eip1014Transition { get; set; }
    long? Eip1052Transition { get; set; }
    long? Eip1108Transition { get; set; }
    long? Eip1283Transition { get; set; }
    long? Eip1283DisableTransition { get; set; }
    long? Eip1344Transition { get; set; }
    long? Eip1559Transition { get; set; }
    long? Eip1884Transition { get; set; }
    long? Eip2028Transition { get; set; }
    long? Eip2200Transition { get; set; }
    long? Eip2565Transition { get; set; }
    long? Eip2929Transition { get; set; }
    long? Eip2930Transition { get; set; }
    long? Eip3198Transition { get; set; }
    long? Eip3529Transition { get; set; }
    long? Eip3541Transition { get; set; }

    // Post-merge — timestamp-activated.
    ulong? Eip1153TransitionTimestamp { get; set; }
    ulong? Eip2537TransitionTimestamp { get; set; }
    ulong? Eip2935TransitionTimestamp { get; set; }
    ulong? Eip3651TransitionTimestamp { get; set; }
    ulong? Eip3855TransitionTimestamp { get; set; }
    ulong? Eip3860TransitionTimestamp { get; set; }
    ulong? Eip4788TransitionTimestamp { get; set; }
    ulong? Eip4844TransitionTimestamp { get; set; }
    ulong? Eip4895TransitionTimestamp { get; set; }
    ulong? Eip5656TransitionTimestamp { get; set; }
    ulong? Eip6110TransitionTimestamp { get; set; }
    ulong? Eip6780TransitionTimestamp { get; set; }
    ulong? Eip7002TransitionTimestamp { get; set; }
    ulong? Eip7251TransitionTimestamp { get; set; }
    ulong? Eip7594TransitionTimestamp { get; set; }
    ulong? Eip7623TransitionTimestamp { get; set; }
    ulong? Eip7702TransitionTimestamp { get; set; }
    ulong? Eip7708TransitionTimestamp { get; set; }
    ulong? Eip7778TransitionTimestamp { get; set; }
    ulong? Eip7823TransitionTimestamp { get; set; }
    ulong? Eip7825TransitionTimestamp { get; set; }
    ulong? Eip7843TransitionTimestamp { get; set; }
    ulong? Eip7883TransitionTimestamp { get; set; }
    ulong? Eip7918TransitionTimestamp { get; set; }
    ulong? Eip7928TransitionTimestamp { get; set; }
    ulong? Eip7934TransitionTimestamp { get; set; }
    ulong? Eip7939TransitionTimestamp { get; set; }
    ulong? Eip7951TransitionTimestamp { get; set; }
    ulong? Eip7954TransitionTimestamp { get; set; }
    ulong? Eip7976TransitionTimestamp { get; set; }
    ulong? Eip7981TransitionTimestamp { get; set; }
    ulong? Eip8024TransitionTimestamp { get; set; }
    ulong? Eip8037TransitionTimestamp { get; set; }
}
