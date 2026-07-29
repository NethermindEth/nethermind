// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.TxPool;

/// <summary>
/// Outcome of resolving an EIP-8141 frame transaction's fee-payer at mempool admission.
/// https://eips.ethereum.org/EIPS/eip-8141
/// </summary>
public enum FrameTxPayerOutcome
{
    /// <summary>A legible validation prefix that sets a payer natively.</summary>
    Resolved,

    /// <summary>A legible validation prefix that provably never sets a payer (an invalid transaction).</summary>
    NoPayer,

    /// <summary>The prefix reaches deployed code the pool cannot evaluate natively; deferred to a later simulation layer.</summary>
    RequiresSimulation,
}

/// <summary>
/// State a legible payer resolution depends on, per EIP-8141 "Direct Evaluation of Protocol-Defined
/// Frames": the sender's code hash and nonce, the payer's code hash and balance, and — when an
/// expiry verifier frame is present — the <c>EXPIRY_VERIFIER</c> code hash and the frame's deadline.
/// </summary>
/// <remarks>
/// Captured so a later revalidation layer can re-check admission on head changes without
/// re-execution. Indexing pending transactions by this set is deferred; the fields are recorded now.
/// https://eips.ethereum.org/EIPS/eip-8141
/// </remarks>
public readonly struct FrameTxDependencySet(
    ValueHash256 senderCodeHash,
    ulong senderNonce,
    Address? payer,
    ValueHash256 payerCodeHash,
    UInt256 payerBalance,
    bool dependsOnExpiry,
    ulong expiryDeadline,
    ValueHash256 expiryVerifierCodeHash)
{
    public ValueHash256 SenderCodeHash { get; } = senderCodeHash;
    public ulong SenderNonce { get; } = senderNonce;

    /// <summary>The resolved payer whose code and balance the resolution depends on; <c>null</c> when unresolved.</summary>
    public Address? Payer { get; } = payer;
    public ValueHash256 PayerCodeHash { get; } = payerCodeHash;
    public UInt256 PayerBalance { get; } = payerBalance;

    /// <summary>True when an expiry verifier frame is present, adding the deadline and block timestamp as dependencies.</summary>
    public bool DependsOnExpiry { get; } = dependsOnExpiry;
    public ulong ExpiryDeadline { get; } = expiryDeadline;
    public ValueHash256 ExpiryVerifierCodeHash { get; } = expiryVerifierCodeHash;
}

/// <summary>
/// Result of <see cref="FrameTxPayerResolver.Resolve"/>: the payer outcome and the state dependency
/// set captured while resolving it.
/// </summary>
public readonly struct FrameTxPayerResolution(FrameTxPayerOutcome outcome, Address? payer, in FrameTxDependencySet dependencies)
{
    public FrameTxPayerOutcome Outcome { get; } = outcome;

    /// <summary>The resolved fee-payer; non-null only when <see cref="Outcome"/> is <see cref="FrameTxPayerOutcome.Resolved"/>.</summary>
    public Address? Payer { get; } = payer;

    public FrameTxDependencySet Dependencies { get; } = dependencies;
}
