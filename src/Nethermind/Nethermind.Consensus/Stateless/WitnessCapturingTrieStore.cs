// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// <see cref="ITrieStore"/> decorator that, when a capture is armed on the
/// <see cref="WitnessCaptureSession"/>, side-channels every resolved node read into the session's
/// <see cref="WitnessTrieStoreRecorder"/>.
/// </summary>
/// <remarks>
/// Adds logic for capturing trie nodes accessed during execution and state root recomputation.
/// Two commit modes:
/// <list type="bullet">
///   <item><b>Read-only (default)</b> — commits are swallowed (<see cref="NullCommitter"/>). Used
///   when wrapping a read-only store for re-execution sandboxes and post-hoc proof collection,
///   where writes must never reach persistence.</item>
///   <item><b>Write-through (<c>readOnly: false</c>)</b> — commits forward verbatim. Used when
///   decorating the live main-world trie store, which persists state; reads are still recorded,
///   but only clean (persisted) nodes — dirty in-memory nodes have no
///   <see cref="TrieNode.Keccak"/>/RLP yet and represent post-state anyway.</item>
/// </list>
/// </remarks>
public class WitnessCapturingTrieStore(ITrieStore baseStore, WitnessCaptureSession session, bool readOnly = true) : ITrieStore
{
    private int _disposed;

    public TrieNode FindCachedOrUnknown(Hash256? address, in TreePath path, Hash256 hash)
    {
        TrieNode node = baseStore.FindCachedOrUnknown(address, in path, hash);
        if (node.NodeType != NodeType.Unknown && session.TrieRecorder is { } recorder)
            recorder.Record(node.Keccak, node.FullRlp.ToArray());
        return node;
    }

    public byte[]? LoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>                                                      
        TryLoadRlp(address, in path, hash, flags)                                                                                                                      
        ?? throw new MissingTrieNodeException("Missing RLP node", address, path, hash);

    public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        byte[]? rlp = baseStore.TryLoadRlp(address, in path, hash, flags);
        if (rlp is not null && session.TrieRecorder is { } recorder) recorder.Record(hash, rlp);
        return rlp;
    }

    public bool HasRoot(Hash256 stateRoot) => baseStore.HasRoot(stateRoot);

    public bool HasRoot(Hash256 stateRoot, long blockNumber) => baseStore.HasRoot(stateRoot, blockNumber);

    public IDisposable BeginScope(BlockHeader? baseBlock) => baseStore.BeginScope(baseBlock);

    // Route through `this` (not baseStore.GetTrieStore) so scoped reads stay captured.
    public IScopedTrieStore GetTrieStore(Hash256? address) => new ScopedTrieStore(this, address);

    public INodeStorage.KeyScheme Scheme => baseStore.Scheme;

    public IBlockCommitter BeginBlockCommit(long blockNumber) =>
        readOnly ? NullCommitter.Instance : baseStore.BeginBlockCommit(blockNumber);

    public ICommitter BeginCommit(Hash256? address, TrieNode? root, WriteFlags writeFlags) =>
        readOnly ? NullCommitter.Instance : baseStore.BeginCommit(address, root, writeFlags);

    /// <remarks>
    /// Dispose-once guard: in write-through mode the dispose stack owns the store's shutdown (cache
    /// persistence), but Autofac also disposes decorator instances at container teardown. The second
    /// call must not reach the inner store — TrieStore.Dispose re-runs PersistOnShutdown against
    /// closed DBs.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) baseStore.Dispose();
    }
}
