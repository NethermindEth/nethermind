// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Eip8288;

/// <summary>A wrapper transaction entry: either the full transaction or, if already broadcast, its hash.</summary>
public readonly struct WrapperTransaction
{
    public Transaction? Full { get; }
    public Hash256? Hash { get; }

    public WrapperTransaction(Transaction full) => Full = full;
    public WrapperTransaction(Hash256 hash) => Hash = hash;

    public bool IsHashOnly => Full is null;
}

/// <summary>
/// EIP-8288 mempool wrapper object <c>[transactions, mode, content]</c>. Bundles frame transactions
/// with either their direct dependency proofs (mode 0) or a single recursive STARK proving all of
/// them (mode 1). It is not itself a valid transaction; mempool nodes, FOCIL creators, and builders
/// all use it to move pre-aggregated proofs around.
/// https://eips.ethereum.org/EIPS/eip-8288
/// </summary>
public sealed class MempoolWrapper
{
    public const byte ModeDirect = 0;
    public const byte ModeRecursive = 1;

    public required IReadOnlyList<WrapperTransaction> Transactions { get; init; }
    public required byte Mode { get; init; }

    /// <summary>Concatenated dependencies of all wrapped transactions (both modes).</summary>
    public required IReadOnlyList<FrameDependency> Deps { get; init; }

    /// <summary>Mode 0: one proof per dependency in <see cref="Deps"/>.</summary>
    public IReadOnlyList<byte[]>? Proofs { get; init; }

    /// <summary>Mode 1: a single recursive STARK over all dependencies, with public input <c>hash(deps)</c>.</summary>
    public RecursiveStark? RecursiveStark { get; init; }
}
