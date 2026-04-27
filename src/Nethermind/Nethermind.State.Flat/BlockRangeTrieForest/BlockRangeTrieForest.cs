// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Trie;

namespace Nethermind.State.Flat.BlockRangeTrieForest;

public class BlockRangeTrieForest(IDb db) : IBlockRangeTrieForest
{
    public byte[]? TryGetState(long blockRange, in TreePath path, in ValueHash256 hash)
    {
        Span<byte> key = stackalloc byte[BlockRangeForestKey.StateKeyLength];
        BlockRangeForestKey.EncodeState(key, blockRange, path, hash);
        return db.Get(key);
    }

    public byte[]? TryGetStorage(long blockRange, in ValueHash256 address, in TreePath path, in ValueHash256 hash)
    {
        Span<byte> key = stackalloc byte[BlockRangeForestKey.StorageKeyLength];
        BlockRangeForestKey.EncodeStorage(key, blockRange, address, path, hash);
        return db.Get(key);
    }

    public IBlockRangeTrieForest.IWriter CreateWriter() => new Writer(db);

    public IBlockRangeTrieForest.IDeletionCursor CreateDeletionCursor(long belowBlockRange, ReadOnlySpan<byte> resumeKey) =>
        new DeletionCursor(db, belowBlockRange, resumeKey);

    private sealed class Writer(IDb db) : IBlockRangeTrieForest.IWriter
    {
        private readonly IWriteBatch _batch = db.StartWriteBatch();

        public void PutState(long blockRange, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> rlp)
        {
            Span<byte> key = stackalloc byte[BlockRangeForestKey.StateKeyLength];
            BlockRangeForestKey.EncodeState(key, blockRange, path, hash);
            _batch.PutSpan(key, rlp);
        }

        public void PutStorage(long blockRange, in ValueHash256 address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> rlp)
        {
            Span<byte> key = stackalloc byte[BlockRangeForestKey.StorageKeyLength];
            BlockRangeForestKey.EncodeStorage(key, blockRange, address, path, hash);
            _batch.PutSpan(key, rlp);
        }

        public void Flush() => _batch.Dispose();

        public void Dispose() => _batch.Dispose();
    }

    private sealed class DeletionCursor : IBlockRangeTrieForest.IDeletionCursor
    {
        private readonly IDb _db;
        private readonly long _belowBlockRange;
        private ISortedView? _view;
        private bool _hasCurrent;
        private byte[] _currentKey = [];

        public DeletionCursor(IDb db, long belowBlockRange, ReadOnlySpan<byte> resumeKey)
        {
            _db = db;
            _belowBlockRange = belowBlockRange;

            byte[] upper = BlockRangeForestKey.RangeUpperBoundKey(belowBlockRange - 1);
            _view = ((ISortedKeyValueStore)db).GetViewBetween(resumeKey, upper);
            _hasCurrent = _view.MoveNext();
            if (!_hasCurrent) DisposeView();
        }

        public bool IsExhausted => !_hasCurrent;

        public byte[] CurrentKey => _currentKey;

        public int DeleteBatch(int count)
        {
            if (!_hasCurrent) return 0;

            int deleted = 0;
            using IWriteBatch batch = _db.StartWriteBatch();
            while (_hasCurrent && deleted < count)
            {
                _currentKey = _view!.CurrentKey.ToArray();
                batch.Remove(_currentKey);
                deleted++;
                _hasCurrent = _view.MoveNext();
            }

            if (!_hasCurrent) DisposeView();
            return deleted;
        }

        private void DisposeView()
        {
            _view?.Dispose();
            _view = null;
        }

        public void Dispose() => DisposeView();
    }
}
