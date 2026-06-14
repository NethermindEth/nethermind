// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.BeaconChain.ForkChoice;

/// <summary>Thrown when a fork-choice handler input violates a consensus-spec assertion (an invalid block, attestation, or slashing).</summary>
public sealed class ForkChoiceException(string message) : Exception(message);
