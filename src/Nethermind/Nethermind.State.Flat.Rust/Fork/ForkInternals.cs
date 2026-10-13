// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.Rust.Fork;

/// <summary>
/// The internal members of Nethermind.Trie, Nethermind.State and Nethermind.State.Flat that the forked copies call,
/// reached without an <c>InternalsVisibleTo</c> edit in those assemblies.
/// </summary>
/// <remarks>
/// The copies name these in place of the original call; <c>ForkDriftTests</c> reverses the substitution when it
/// compares a copy with its origin and pins <see cref="KeyHashMinimumBatchSize"/> and
/// <see cref="FlatDbManagerLoggerName"/> to the original values.
/// </remarks>
internal static class ForkInternals
{
    /// <summary>The value of <c>Nethermind.State.KeyHashBatch.MinimumBatchSize</c>.</summary>
    internal const int KeyHashMinimumBatchSize = 4;

    /// <summary>
    /// The logger name of <c>FlatDbManager</c>, whose lines the fork's manager logs, given as text since the fork names
    /// no original type it replaces.
    /// </summary>
    internal const string FlatDbManagerLoggerName = "State.Flat.FlatDbManager";

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "MarkWarmerOwned")]
    internal static extern void MarkWarmerOwned(TrieNode node);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "CreateBulkSetEntry")]
    internal static extern PatriciaTree.BulkSetEntry CreateBulkSetEntry(StorageTree? _, in ValueHash256 key, ReadOnlySpan<byte> value, bool isZero);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SetAccounts")]
    internal static extern void SetAccounts(StateTree tree, Dictionary<AddressAsKey, Account?> accounts);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "VerifyCore")]
    internal static extern bool VerifyCore(Nethermind.State.Flat.FlatTrieVerifier verifier, IPersistence.IPersistenceReader reader,
        IScopedTrieStore trieStore, Hash256 stateRoot, CancellationToken cancellationToken);
}
