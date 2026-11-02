// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>
/// EIP-8288 mempool wrapper <c>[transactions, mode, content]</c>: full frame transactions with either their direct
/// dependency proofs (mode 0) or a single recursive STARK over all of them (mode 1). Not a valid transaction; used to move
/// transactions and their proofs between mempool actors.
/// </summary>
public sealed class MempoolWrapper
{
    public const byte ModeDirect = 0;
    public const byte ModeRecursive = 1;

    /// <summary>Full EIP-8141 transactions in their pooled network form, in strictly ascending transaction-hash order.</summary>
    /// <remarks>Each <see cref="Transaction.Hash"/> is the consensus hash, which excludes any blob network wrapper.</remarks>
    public required IReadOnlyList<Transaction> Transactions { get; init; }
    public required byte Mode { get; init; }

    /// <summary>Sorted, deduplicated union of wrapped transaction dependencies.</summary>
    public required IReadOnlyList<FrameDependency> Deps { get; init; }

    /// <summary>Mode 0: one proof per dependency in <see cref="Deps"/>.</summary>
    public IReadOnlyList<byte[]>? Proofs { get; init; }

    /// <summary>Mode 1: a single recursive STARK over all dependencies, with public input <c>hash(deps)</c>.</summary>
    public RecursiveStark? RecursiveStark { get; init; }
}
