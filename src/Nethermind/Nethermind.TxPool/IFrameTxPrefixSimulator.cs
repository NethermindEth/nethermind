// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;

namespace Nethermind.TxPool;

/// <summary>Runs the validation prefix of an EIP-8141 frame transaction the native resolver could not decide, in a bounded read-only EVM at chain head.</summary>
/// <remarks>Optional: with no simulator wired, such transactions are admitted unresolved and hold no exposure reservation.</remarks>
public interface IFrameTxPrefixSimulator
{
    /// <param name="signaturesPreValidated">Assert only if this exact transaction has already passed
    /// <c>validate_signature</c> at chain head; the simulation then trusts its signatures. The two sides read
    /// the head separately, so a head change between them can only mis-admit, never mis-reject.</param>
    /// <param name="token">Checked at entry and, once cancelled, aborts a running simulation at the next EVM
    /// cancellation checkpoint. Independently, the implementation applies a best-effort wall-clock budget over
    /// the EVM run and leaves a transaction that exceeds it undecided; the budget is opcode-granular, not a hard
    /// deadline. The same budget bounds the wait for the shared executor, so contention alone can return
    /// undecided before the EVM runs.</param>
    FrameTxSimulationResult Simulate(Transaction tx, bool signaturesPreValidated = false, CancellationToken token = default);
}

public enum FrameTxSimulationOutcome
{
    /// <summary>A node-side fault stopped the simulation before it could judge the transaction.</summary>
    /// <remarks>The zero value, so a default-constructed result defers rather than recording a null payer.</remarks>
    Undecided,

    /// <summary>The validation prefix ran to a resolved payer.</summary>
    Accepted,

    /// <summary>The prefix is invalid, and the failure is attributable to the transaction.</summary>
    Rejected,
}

public readonly struct FrameTxSimulationResult(FrameTxSimulationOutcome outcome, Address? payer, string? reason)
{
    public FrameTxSimulationOutcome Outcome { get; } = outcome;

    /// <summary>Non-null only when <see cref="Outcome"/> is <see cref="FrameTxSimulationOutcome.Accepted"/>.</summary>
    public Address? Payer { get; } = payer;

    public string? Reason { get; } = reason;

    public static FrameTxSimulationResult Accept(Address payer) => new(FrameTxSimulationOutcome.Accepted, payer, null);
    public static FrameTxSimulationResult Reject(string reason) => new(FrameTxSimulationOutcome.Rejected, null, reason);

    /// <summary>The node, not the transaction, is at fault, so the caller must not turn this into a rejection.</summary>
    public static FrameTxSimulationResult Undecided(string reason) => new(FrameTxSimulationOutcome.Undecided, null, reason);
}
