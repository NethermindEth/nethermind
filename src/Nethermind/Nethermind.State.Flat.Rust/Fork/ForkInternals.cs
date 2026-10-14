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
/// <para>
/// The fork in this namespace, which <c>Backend=Rust</c> runs on, copies files of Nethermind.State.Flat as of commit
/// f4c7c182dd; the manifest of <c>ForkDriftTests</c> lists them with their origin's SHA-256. To re-port after an
/// origin changes, run the explicit <c>ForkDriftTests.Regenerate_verbatim_copies</c> for the verbatim copies, and
/// port the patched <c>SnapshotBundle</c> and the <c>FlatTrieVerifier</c> wrapper by hand, updating their SHA-256
/// in the same commit.
/// </para>
/// <para>
/// The copies name these in place of the original call; <c>ForkDriftTests</c> reverses the substitution when it
/// compares a copy with its origin and pins <see cref="KeyHashMinimumBatchSize"/> and
/// <see cref="FlatDbManagerLoggerName"/> to the original values.
/// </para>
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
