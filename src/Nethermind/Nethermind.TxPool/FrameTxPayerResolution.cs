// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

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

/// <summary>Chain-head state a legible payer resolution depends on, captured so admission can be revalidated on head changes without re-execution.</summary>
internal readonly struct FrameTxDependencySet(
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

    /// <summary>The resolved payer; <c>null</c> when unresolved.</summary>
    public Address? Payer { get; } = payer;
    public ValueHash256 PayerCodeHash { get; } = payerCodeHash;
    public UInt256 PayerBalance { get; } = payerBalance;

    public bool DependsOnExpiry { get; } = dependsOnExpiry;
    public ulong ExpiryDeadline { get; } = expiryDeadline;
    public ValueHash256 ExpiryVerifierCodeHash { get; } = expiryVerifierCodeHash;
}

internal readonly struct FrameTxPayerResolution(FrameTxPayerOutcome outcome, Address? payer, in FrameTxDependencySet dependencies)
{
    public FrameTxPayerOutcome Outcome { get; } = outcome;

    /// <summary>The resolved fee-payer; non-null only when <see cref="Outcome"/> is <see cref="FrameTxPayerOutcome.Resolved"/>.</summary>
    public Address? Payer { get; } = payer;

    public FrameTxDependencySet Dependencies { get; } = dependencies;
}
