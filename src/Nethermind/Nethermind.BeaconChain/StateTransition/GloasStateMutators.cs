// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Types;

namespace Nethermind.BeaconChain.StateTransition;

/// <summary>
/// Slashing over <see cref="BeaconStateGloas"/>: the one <see cref="BeaconStateMutators"/> entry
/// the Gloas block operations need that <see cref="GloasStateAccessors"/> does not already carry.
/// </summary>
/// <remarks>
/// The Electra semantics of <see cref="BeaconStateMutators.SlashValidator"/>, plus the Gloas
/// (EIP-7732) removal of the slashed validator's pending builder payments; duplicated rather than
/// shared for the reason given on <see cref="GloasStateAccessors"/>. The exit it initiates draws on the Gloas exit churn
/// (EIP-8061) through <see cref="GloasStateAccessors.InitiateValidatorExit"/>.
/// </remarks>
public static partial class GloasStateMutators
{
    public static partial void SlashValidator(this BeaconStateGloas state, int slashedIndex, EpochCache cache, int? whistleblowerIndex = null);
}
