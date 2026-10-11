// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


namespace Nethermind.BeaconChain.StateTransition;

/// <summary>
/// Thrown when a state-transition spec assertion fails, e.g. an invalid attestation or an
/// out-of-range state access. Maps to a Python <c>assert</c> in consensus-specs, so catching it
/// during block processing means the block is invalid.
/// </summary>
public class BeaconStateException(string message) : Exception(message)
{
    internal bool RejectGossip { get; init; }
}

/// <summary>
/// Thrown when a block's proposer signature is invalid. The signature is not part of the block root,
/// so it says nothing about the block a root names.
/// </summary>
public sealed class ProposerSignatureException(string message) : BeaconStateException(message);
