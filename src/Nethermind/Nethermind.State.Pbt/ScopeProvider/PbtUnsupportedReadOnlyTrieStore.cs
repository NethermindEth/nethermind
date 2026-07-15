// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Pbt.ScopeProvider;

/// <summary>
/// The PBT backend has no Patricia trie to expose; every member throws. Only reached by
/// trie-bound features (e.g. witness generation) that are unsupported on this backend.
/// </summary>
public sealed class PbtUnsupportedReadOnlyTrieStore : IReadOnlyTrieStore
{
    private static NotSupportedException Unsupported() => new("The pbt state backend has no Patricia trie store");

    public bool HasRoot(Hash256 stateRoot) => throw Unsupported();

    public IDisposable BeginScope(BlockHeader? baseBlock) => throw Unsupported();

    public IScopedTrieStore GetTrieStore(Hash256? address) => throw Unsupported();

    public IBlockCommitter BeginBlockCommit(ulong blockNumber) => throw Unsupported();

    public ICommitter BeginCommit(Hash256? address, TrieNode? root, WriteFlags writeFlags) => throw Unsupported();

    public TrieNode FindCachedOrUnknown(Hash256? address, in TreePath path, Hash256 hash) => throw Unsupported();

    public byte[]? LoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => throw Unsupported();

    public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => throw Unsupported();

    public INodeStorage.KeyScheme Scheme => throw Unsupported();

    public void Dispose()
    {
    }
}
