// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;

namespace Nethermind.TxPool;

/// <summary>
/// Runs the validation prefix of an EIP-8141 frame transaction whose payer the native resolver could not
/// decide (<see cref="FrameTxPayerOutcome.RequiresSimulation"/>) in a bounded, read-only EVM at chain head.
/// </summary>
/// <remarks>
/// Optional in the pool: with no simulator wired, such transactions are admitted with an unresolved payer
/// and therefore without an exposure reservation. https://eips.ethereum.org/EIPS/eip-8141
/// </remarks>
public interface IFrameTxPrefixSimulator
{
    /// <summary>Simulates <paramref name="tx"/>'s validation prefix against the current head.</summary>
    /// <param name="token">Honored at entry only; a started simulation runs to its <c>MAX_VERIFY_GAS</c> bound.</param>
    FrameTxSimulationResult Simulate(Transaction tx, CancellationToken token = default);
}

/// <summary>Outcome of <see cref="IFrameTxPrefixSimulator.Simulate"/>.</summary>
public readonly struct FrameTxSimulationResult(bool accepted, Address? payer, string? rejectionReason)
{
    /// <summary>True when the prefix validated and set a payer within the gas bound.</summary>
    public bool Accepted { get; } = accepted;

    /// <summary>The resolved fee-payer; non-null only when <see cref="Accepted"/> is true.</summary>
    public Address? Payer { get; } = payer;

    /// <summary>Human-readable rejection reason; null when accepted.</summary>
    public string? RejectionReason { get; } = rejectionReason;

    public static FrameTxSimulationResult Accept(Address payer) => new(true, payer, null);
    public static FrameTxSimulationResult Reject(string reason) => new(false, null, reason);
}
