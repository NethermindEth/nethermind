// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>
/// Signals that a deadline-bound production pass would have to wait for native proving of its dependency set.
/// </summary>
/// <remarks>
/// The missing proof is scheduled in the background; the producer rebuilds the body restricted to
/// <see cref="Proven"/>, a dependency set whose verified proof already exists.
/// </remarks>
public sealed class LeanProofNotReadyException(IReadOnlySet<FrameDependency> proven)
    : Exception("EIP-8288 dependency proof is not ready for this production deadline.")
{
    /// <summary>The largest attempted dependency subset that already has a verified recursive proof.</summary>
    public IReadOnlySet<FrameDependency> Proven { get; } = proven;
}
