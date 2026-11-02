// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Consensus.Transactions;

/// <summary>
/// The block a transaction is checked for: its parent, its spec and its base fee, passed with every
/// <see cref="ITxFilter"/> call so that filters read the base fee instead of computing it for each transaction.
/// </summary>
/// <remarks>
/// Block building checks every candidate against the same parent and spec, so it builds one context per block with
/// the base fee it has already computed. A caller that checks a single transaction builds one with
/// <see cref="TxFilterContext(BlockHeader, IReleaseSpec)"/>.
/// </remarks>
/// <param name="parent">The parent of the block the transaction is checked for.</param>
/// <param name="spec">The spec of the block the transaction is checked for.</param>
/// <param name="baseFee">The base fee the caller has already computed with <see cref="BaseFeeCalculator.Calculate"/>
/// for <paramref name="parent"/> and <paramref name="spec"/>.</param>
public readonly struct TxFilterContext(BlockHeader parent, IReleaseSpec spec, in UInt256 baseFee)
{
    /// <summary>Computes the next block's base fee from <paramref name="parent"/> when <paramref name="spec"/> has
    /// EIP-1559, as the filters did for each transaction.</summary>
    public TxFilterContext(BlockHeader parent, IReleaseSpec spec)
        : this(parent, spec, spec.IsEip1559Enabled ? BaseFeeCalculator.Calculate(parent, spec) : UInt256.Zero)
    {
    }

    /// <summary>The parent of the block the transaction is checked for.</summary>
    public BlockHeader Parent { get; } = parent;

    /// <summary>The spec of the block the transaction is checked for, not of its parent.</summary>
    public IReleaseSpec Spec { get; } = spec;

    /// <summary>The base fee of the block the transaction is checked for.</summary>
    /// <remarks>Meaningful only when <see cref="Spec"/> has EIP-1559; filters do not read it otherwise.</remarks>
    public UInt256 BaseFee { get; } = baseFee;
}
