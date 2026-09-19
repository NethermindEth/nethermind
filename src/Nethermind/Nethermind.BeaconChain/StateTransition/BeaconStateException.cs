// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.BeaconChain.StateTransition;

/// <summary>
/// Thrown when a state-transition spec assertion fails, e.g. an invalid attestation or an
/// out-of-range state access. Maps to a Python <c>assert</c> in consensus-specs, so catching it
/// during block processing means the block is invalid.
/// </summary>
public class BeaconStateException(string message) : Exception(message);
