// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>
/// EIP-8288 mempool wrapper <c>[transactions, mode, content]</c>: full frame transactions with either their direct
/// dependency proofs (mode 0), a single recursive STARK over all of them (mode 1), or EIP-8437's raw signatures for recognized
/// leaf programs alongside direct proofs (mode 2). Not a valid transaction; used to move transactions and their proofs between
/// mempool actors.
/// </summary>
public sealed class MempoolWrapper
{
    public const byte ModeDirect = 0;
    public const byte ModeRecursive = 1;
    public const byte ModeRaw = 2;

    /// <summary>EIP-8437's bound on one raw entry's complete RLP encoding.</summary>
    public const int MaxRawWitnessBytes = 16 * 1024;

    /// <summary>EIP-8437's bound on a mode-2 wrapper's raw entries.</summary>
    public const int MaxRawEntries = 16;

    /// <summary>EIP-8437's bound on a mode-2 wrapper's transactions, counting those sharing a dependency.</summary>
    public const int MaxRawTransactions = 64;

    /// <summary>Full EIP-8141 transactions in their pooled network form, in strictly ascending transaction-hash order.</summary>
    /// <remarks>Each <see cref="Transaction.Hash"/> is the consensus hash, which excludes any blob network wrapper.</remarks>
    public required IReadOnlyList<Transaction> Transactions { get; init; }
    public required byte Mode { get; init; }

    /// <summary>Sorted, deduplicated union of wrapped transaction dependencies.</summary>
    public required IReadOnlyList<FrameDependency> Deps { get; init; }

    /// <summary>Modes 0 and 2: one proof per dependency in <see cref="Deps"/>; empty where mode 2 carries a raw witness.</summary>
    public IReadOnlyList<byte[]>? Proofs { get; init; }

    /// <summary>Mode 2: one entry per dependency in <see cref="Deps"/>, the raw witness where there is one and null where
    /// <see cref="Proofs"/> holds an ordinary witness.</summary>
    public IReadOnlyList<RawSignature?>? RawWitnesses { get; init; }

    /// <summary>Mode 1: a single recursive STARK over all dependencies, with public input <c>hash(deps)</c>.</summary>
    public RecursiveStark? RecursiveStark { get; init; }
}
