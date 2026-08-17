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

public readonly struct FrameTxSimulationResult(bool accepted, Address? payer, string? rejectionReason, bool indeterminate = false, bool nodeBound = false)
{
    public bool Accepted { get; } = accepted;

    /// <summary>Non-null only when <see cref="Accepted"/>.</summary>
    public Address? Payer { get; } = payer;

    public string? RejectionReason { get; } = rejectionReason;

    /// <summary>
    /// True when the rejection reflects an admission bound rather than the prefix, so it says nothing
    /// about validity.
    /// </summary>
    /// <remarks>No production code reads this yet: admission is driven by <see cref="NodeBound"/>, and the
    /// retention it is meant to govern arrives with a revalidation pass that consults the simulator.</remarks>
    public bool Indeterminate { get; } = indeterminate;

    /// <summary>
    /// True when the bound was one this node imposed on itself, so the sending peer did not choose it.
    /// A timeout is indeterminate but <em>not</em> node-bound: the prefix's own wall clock trips it.
    /// </summary>
    public bool NodeBound { get; } = nodeBound;

    public static FrameTxSimulationResult Accept(Address payer) => new(true, payer, null);
    public static FrameTxSimulationResult Reject(string reason) => new(false, null, reason);

    /// <summary>A rejection caused by a bound this node spent on itself, not by the prefix.</summary>
    public static FrameTxSimulationResult RejectIndeterminate(string reason) => new(false, null, reason, indeterminate: true, nodeBound: true);

    /// <summary>A rejection the prefix's own wall-clock consumption caused; retained, but chargeable to the sender.</summary>
    public static FrameTxSimulationResult RejectTimedOut(string reason) => new(false, null, reason, indeterminate: true);
}
