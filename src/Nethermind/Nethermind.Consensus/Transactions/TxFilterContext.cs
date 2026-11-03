// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Consensus.Transactions;

/// <summary>
/// The block a transaction is checked for: its parent, its spec and its base fee, passed with every
/// <see cref="ITxFilter"/> call.
/// </summary>
/// <remarks>
/// Block building checks every candidate against the same parent and spec, so it supplies the base fee it has already
/// computed with <see cref="TxFilterContext(BlockHeader, IReleaseSpec, in UInt256)"/>. A caller that does not need the
/// base fee itself, such as tx-pool admission or a source that filters another source's transactions, uses
/// <see cref="TxFilterContext(BlockHeader, IReleaseSpec)"/>, which computes the base fee only when a filter reads it.
/// </remarks>
/// <param name="parent">The parent of the block the transaction is checked for.</param>
/// <param name="spec">The spec of the block the transaction is checked for.</param>
public readonly struct TxFilterContext(BlockHeader parent, IReleaseSpec spec)
{
    private readonly UInt256 _baseFee;
    private readonly bool _hasBaseFee;

    /// <summary>Uses a base fee the caller has already computed with <see cref="BaseFeeCalculator.Calculate"/> for
    /// <paramref name="parent"/> and <paramref name="spec"/>.</summary>
    public TxFilterContext(BlockHeader parent, IReleaseSpec spec, in UInt256 baseFee) : this(parent, spec)
    {
        _baseFee = baseFee;
        _hasBaseFee = true;
    }

    /// <summary>The parent of the block the transaction is checked for.</summary>
    public BlockHeader Parent { get; } = parent;

    /// <summary>The spec of the block the transaction is checked for, not of its parent.</summary>
    public IReleaseSpec Spec { get; } = spec;

    /// <summary>The base fee of the block the transaction is checked for.</summary>
    /// <remarks>
    /// Filters read it only when <see cref="Spec"/> has EIP-1559, each filter at most once per call; a wrapper that runs
    /// two filters that read it reads it twice. Unless the caller supplied it, every read computes it with
    /// <see cref="BaseFeeCalculator.Calculate"/> from <see cref="Parent"/> and <see cref="Spec"/>, so a context nobody
    /// reads it from costs nothing, and a null parent fails where the calculator fails.
    /// </remarks>
    public UInt256 BaseFee => _hasBaseFee ? _baseFee : BaseFeeCalculator.Calculate(Parent, Spec);
}
