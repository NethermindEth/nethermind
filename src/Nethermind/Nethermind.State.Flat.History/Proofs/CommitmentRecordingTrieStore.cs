// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.History.Proofs;

internal sealed class CommitmentRecordingTrieStore : IScopedTrieStore
{
    private readonly IScopedTrieStore _inner;
    private readonly CommitmentEmitter _emitter;
    private readonly ValueHash256? _storageAccount;
    private readonly int _minRecordedDepth;
    private readonly RecordingCommitter _committer;
    private TrieNode? _committedRoot;

    public CommitmentRecordingTrieStore(IScopedTrieStore inner, CommitmentEmitter emitter, ValueHash256? storageAccount, int minRecordedDepth = 0)
    {
        _inner = inner;
        _emitter = emitter;
        _storageAccount = storageAccount;
        _minRecordedDepth = minRecordedDepth;
        _committer = new RecordingCommitter(this);
    }

    public bool Recording { get; set; } = true;

    public ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) => _committer;

    public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
        path.Length == 0 && _committedRoot is { } root && root.Keccak == hash ? root : _inner.FindCachedOrUnknown(path, hash);

    public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => _inner.LoadRlp(path, hash, flags);

    public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => _inner.TryLoadRlp(path, hash, flags);

    public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) => _inner.GetStorageTrieNodeResolver(address);

    public INodeStorage.KeyScheme Scheme => _inner.Scheme;

    private void Record(in TreePath path, TrieNode node)
    {
        if (path.Length == 0) _committedRoot = node;
        if (!Recording || path.Length < _minRecordedDepth) return;

        if (_storageAccount is { } account) _emitter.RecordStorageNode(account, path, node.FullRlp.AsSpan());
        else _emitter.RecordAccountNode(path, node.FullRlp.AsSpan());
    }

    private sealed class RecordingCommitter(CommitmentRecordingTrieStore owner) : ICommitter
    {
        public TrieNode CommitNode(ref TreePath path, TrieNode node)
        {
            owner.Record(path, node);
            return node;
        }

        public void Dispose()
        {
        }
    }
}
