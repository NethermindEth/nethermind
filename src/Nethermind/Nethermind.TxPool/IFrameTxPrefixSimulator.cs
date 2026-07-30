// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;

namespace Nethermind.TxPool;

/// <summary>
/// Simulates the validation prefix of an EIP-8141 frame transaction whose payer the native resolver
/// could not decide (<see cref="FrameTxPayerOutcome.RequiresSimulation"/>) — a deployed-code sender,
/// a code-carrying paymaster, or an unrecognized shape — against the current chain head.
/// </summary>
/// <remarks>
/// Narrow abstraction so <c>Nethermind.TxPool</c> need not reference the read-only processing env in
/// <c>Nethermind.Consensus</c> (which already references TxPool — a direct reference would cycle). The
/// implementation, wired at the composition root, runs the prefix in a bounded, read-only EVM under
/// <c>MAX_VERIFY_GAS</c> and enforces the trace/opcode rules. Injected optionally into the pool
/// (mirroring the optional incoming-tx filter): when absent, <c>RequiresSimulation</c> frame txs stay
/// rejected as in Phase 1. https://eips.ethereum.org/EIPS/eip-8141 (ethereum/EIPs#12007)
/// </remarks>
public interface IFrameTxPrefixSimulator
{
    /// <summary>Simulates <paramref name="tx"/>'s validation prefix against the current head.</summary>
    /// <param name="tx">The frame transaction whose validation prefix is simulated.</param>
    /// <param name="token">
    /// Cancels the (up to <c>MAX_VERIFY_GAS</c>) simulation, which may also block behind other peers'
    /// serialized simulations. Honored at entry; per-frame cooperative cancellation is a deferred
    /// follow-up (design note §4). An <see cref="System.OperationCanceledException"/> propagates.
    /// </param>
    FrameTxSimulationResult Simulate(Transaction tx, CancellationToken token = default);
}

/// <summary>
/// Outcome of an <see cref="IFrameTxPrefixSimulator.Simulate"/> call: whether the prefix is admissible
/// and, when it is, the payer it resolved.
/// </summary>
public readonly struct FrameTxSimulationResult(bool accepted, Address? payer, string? rejectionReason)
{
    /// <summary>True when the prefix validated under the trace/opcode rules and set a payer within the gas bound.</summary>
    public bool Accepted { get; } = accepted;

    /// <summary>The resolved fee-payer; non-null only when <see cref="Accepted"/> is true.</summary>
    public Address? Payer { get; } = payer;

    /// <summary>Human-readable reason the prefix was rejected; null when accepted.</summary>
    public string? RejectionReason { get; } = rejectionReason;

    public static FrameTxSimulationResult Accept(Address payer) => new(true, payer, null);
    public static FrameTxSimulationResult Reject(string reason) => new(false, null, reason);
}
