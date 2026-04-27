// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core;
using Nethermind.Trie;

namespace Nethermind.State.Flat.BlockRangeTrieForest;

public sealed class NullBlockRangeTrieForest : IBlockRangeTrieForest
{
    public static readonly NullBlockRangeTrieForest Instance = new();

    public byte[]? TryGetState(long blockRange, in TreePath path, in ValueHash256 hash) => null;
    public byte[]? TryGetStorage(long blockRange, in ValueHash256 address, in TreePath path, in ValueHash256 hash) => null;
    public IBlockRangeTrieForest.IWriter CreateWriter() => NullWriter.Instance;
    public IBlockRangeTrieForest.IDeletionCursor CreateDeletionCursor(long belowBlockRange, ReadOnlySpan<byte> resumeKey) => NullCursor.Instance;

    private sealed class NullWriter : IBlockRangeTrieForest.IWriter
    {
        public static readonly NullWriter Instance = new();
        public void PutState(long blockRange, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> rlp) { }
        public void PutStorage(long blockRange, in ValueHash256 address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> rlp) { }
        public void Flush() { }
        public void Dispose() { }
    }

    private sealed class NullCursor : IBlockRangeTrieForest.IDeletionCursor
    {
        public static readonly NullCursor Instance = new();
        public bool IsExhausted => true;
        public byte[] CurrentKey => [];
        public int DeleteBatch(int count) => 0;
        public void Dispose() { }
    }
}
