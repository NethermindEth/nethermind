// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.TxPool;

internal enum FrameTxPayerOutcome
{
    /// <summary>Payer set natively from a legible validation prefix.</summary>
    Resolved,

    /// <summary>Legible prefix that provably never sets a payer (an invalid transaction).</summary>
    NoPayer,

    /// <summary>Reaches deployed code the pool cannot evaluate natively, or names a third-party payer; deferred to the simulation layer.</summary>
    RequiresSimulation,
}

internal readonly struct FrameTxPayerResolution(FrameTxPayerOutcome outcome, Address? payer)
{
    public FrameTxPayerOutcome Outcome { get; } = outcome;

    /// <summary>The resolved fee-payer; non-null only when <see cref="Outcome"/> is <see cref="FrameTxPayerOutcome.Resolved"/>.</summary>
    public Address? Payer { get; } = payer;
}
