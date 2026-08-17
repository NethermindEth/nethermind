// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;

namespace Nethermind.TxPool;

/// <summary>
/// Runs the validation prefix of an EIP-8141 frame transaction whose payer the native resolver could not
/// decide (<see cref="FrameTxPayerOutcome.RequiresSimulation"/>) in a bounded, read-only EVM at chain head.
/// </summary>
/// <remarks>Optional in the pool: with no simulator wired, such transactions are admitted with an
/// unresolved payer and therefore without an exposure reservation.</remarks>
public interface IFrameTxPrefixSimulator
{
    /// <param name="token">Honored at entry and polled cooperatively during execution. It does not bound the
    /// wait for the serialized processing env; the implementation's own timeout does, as a rejection.</param>
    /// <param name="local">Exempt from the per-head budget that rations simulation between gossiping peers;
    /// the per-simulation timeout and <c>MAX_VERIFY_GAS</c> still apply. Assumes a trusted RPC: publicly
    /// exposed, it is the one admission path with no cumulative bound.</param>
    FrameTxSimulationResult Simulate(Transaction tx, CancellationToken token = default, bool local = false);
}

public enum FrameTxSimulationOutcome
{
    /// <summary>The validation prefix ran to a resolved payer.</summary>
    Accepted,

    /// <summary>The prefix is invalid, and the failure is attributable to the transaction.</summary>
    Rejected,

    /// <summary>A node-side fault stopped the simulation before it could judge the transaction.</summary>
    Undecided,
}

public readonly struct FrameTxSimulationResult(
    FrameTxSimulationOutcome outcome,
    Address? payer,
    string? reason,
    bool indeterminate = false,
    bool nodeBound = false)
{
    public FrameTxSimulationOutcome Outcome { get; } = outcome;

    /// <summary>Non-null only when <see cref="Outcome"/> is <see cref="FrameTxSimulationOutcome.Accepted"/>.</summary>
    public Address? Payer { get; } = payer;

    public string? Reason { get; } = reason;

    /// <summary>
    /// True when the outcome reflects an admission bound or a node fault rather than the prefix, so it
    /// says nothing about validity.
    /// </summary>
    /// <remarks>Admission still declines, but revalidation must leave such a transaction pending: evicting
    /// on an exhausted budget would turn a bound into a mass eviction.</remarks>
    public bool Indeterminate { get; } = indeterminate;

    /// <summary>
    /// True when the bound was one this node imposed on itself, so the sending peer did not choose it.
    /// A timeout is indeterminate but <em>not</em> node-bound: the prefix's own wall clock trips it.
    /// </summary>
    public bool NodeBound { get; } = nodeBound;

    public static FrameTxSimulationResult Accept(Address payer) => new(FrameTxSimulationOutcome.Accepted, payer, null);
    public static FrameTxSimulationResult Reject(string reason) => new(FrameTxSimulationOutcome.Rejected, null, reason);

    /// <summary>A rejection caused by a bound this node spent on itself, not by the prefix. Still charged to
    /// the peer as load, because shedding is what the throttle is for.</summary>
    public static FrameTxSimulationResult RejectIndeterminate(string reason) => new(FrameTxSimulationOutcome.Rejected, null, reason, indeterminate: true, nodeBound: true);

    /// <summary>A rejection the prefix's own wall-clock consumption caused; retained, but chargeable to the sender.</summary>
    public static FrameTxSimulationResult RejectTimedOut(string reason) => new(FrameTxSimulationOutcome.Rejected, null, reason, indeterminate: true);

    /// <summary>The node malfunctioned rather than shed load, so the caller must not turn this into a
    /// rejection at all.</summary>
    public static FrameTxSimulationResult Undecided(string reason) => new(FrameTxSimulationOutcome.Undecided, null, reason, indeterminate: true, nodeBound: true);
}
