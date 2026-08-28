// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.Init;

/// <summary>
/// Wraps the pruning trie store's node storage so that, immediately before its write-ahead log or memtable
/// is flushed - the point at which a block's state becomes durable - all deferred block-data is drained and
/// fsynced.
/// </summary>
/// <remarks>
/// Keeps the <c>state(N) durable =&gt; block-data(&lt;= N) durable</c> invariant without the trie store having
/// to know about the persistence barrier. A fault raised by the drain propagates out of <see cref="Flush"/>,
/// aborting the persist rather than letting state outlive its block-data.
/// </remarks>
internal sealed class BarrierNodeStorage(INodeStorage inner, IStatePersistenceBarrier barrier) : INodeStorage
{
    public INodeStorage.KeyScheme Scheme { get => inner.Scheme; set => inner.Scheme = value; }

    public bool RequirePath => inner.RequirePath;

    public byte[]? Get(Hash256? address, in TreePath path, in ValueHash256 keccak, ReadFlags readFlags = ReadFlags.None)
        => inner.Get(address, in path, in keccak, readFlags);

    public void Set(Hash256? address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> data, WriteFlags writeFlags = WriteFlags.None)
        => inner.Set(address, in path, in hash, data, writeFlags);

    public INodeStorage.IWriteBatch StartWriteBatch() => inner.StartWriteBatch();

    public bool KeyExists(in ValueHash256? address, in TreePath path, in ValueHash256 hash)
        => inner.KeyExists(in address, in path, in hash);

    public void Flush(bool onlyWal)
    {
        barrier.FlushDeferred();
        inner.Flush(onlyWal);
    }

    public void Compact() => inner.Compact();
}
