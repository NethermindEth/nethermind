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
/// <c>Nethermind.Consensus</c> (which already references TxPool — a direct reference would cycle).
/// Injected optionally: when absent, an opaque frame transaction is left unresolved rather than
/// admitted. https://eips.ethereum.org/EIPS/eip-8141
/// </remarks>
public interface IFrameTxPrefixSimulator
{
    /// <summary>Simulates <paramref name="tx"/>'s validation prefix against the current head.</summary>
    /// <param name="tx">The frame transaction whose validation prefix is simulated.</param>
    /// <param name="token">
    /// Cancels the simulation cooperatively (the interpreter polls it) and bounds the wait for the
    /// serialized processing env. An <see cref="System.OperationCanceledException"/> propagates; the
    /// implementation's own wall-clock bound surfaces as a rejection instead.
    /// </param>
    FrameTxSimulationResult Simulate(Transaction tx, CancellationToken token = default);
}

/// <summary>
/// Outcome of an <see cref="IFrameTxPrefixSimulator.Simulate"/> call: whether the prefix is admissible
/// and, when it is, the payer it resolved.
/// </summary>
public readonly struct FrameTxSimulationResult(bool accepted, Address? payer, string? rejectionReason, bool indeterminate = false)
{
    /// <summary>True when the prefix validated under the trace/opcode rules and set a payer within the gas bound.</summary>
    public bool Accepted { get; } = accepted;

    /// <summary>The resolved fee-payer; non-null only when <see cref="Accepted"/> is true.</summary>
    public Address? Payer { get; } = payer;

    /// <summary>Human-readable reason the prefix was rejected; null when accepted.</summary>
    public string? RejectionReason { get; } = rejectionReason;

    /// <summary>
    /// True when the rejection reflects a resource bound rather than the prefix itself, so it says
    /// nothing about validity.
    /// </summary>
    /// <remarks>
    /// Admission still rejects (declining is mempool-legal), but revalidation must leave such a
    /// transaction pending: evicting on an exhausted budget would turn a bound into a mass eviction.
    /// </remarks>
    public bool Indeterminate { get; } = indeterminate;

    public static FrameTxSimulationResult Accept(Address payer) => new(true, payer, null);
    public static FrameTxSimulationResult Reject(string reason) => new(false, null, reason);

    /// <summary>A rejection caused by a resource bound, not by the prefix.</summary>
    public static FrameTxSimulationResult RejectIndeterminate(string reason) => new(false, null, reason, indeterminate: true);
}
