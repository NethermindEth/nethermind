// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

/// <summary>
/// Read-only node storage operations used to resolve trie nodes by their storage key.
/// </summary>
public interface IReadOnlyNodeStorage
{
    /// <summary>
    /// What is the current key scheme
    /// </summary>
    public INodeStorage.KeyScheme Scheme { get; }

    /// <summary>
    /// When running completely from hash based db, some code path that calculate path can be ignored.
    /// </summary>
    public bool RequirePath { get; }

    byte[]? Get(Hash256? address, in TreePath path, in ValueHash256 keccak, ReadFlags readFlags = ReadFlags.None);

    /// <summary>
    /// Used by StateSync
    /// </summary>
    bool KeyExists(in ValueHash256? address, in TreePath path, in ValueHash256 hash);
}

public interface INodeStorage : IReadOnlyNodeStorage
{
    /// <inheritdoc cref="IReadOnlyNodeStorage.Scheme" />
    public new KeyScheme Scheme { get; set; }

    void Set(Hash256? address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> data, WriteFlags writeFlags = WriteFlags.None);
    IWriteBatch StartWriteBatch();

    /// <summary>
    /// Used by StateSync to make sure values are flushed.
    /// </summary>
    /// <param name="onlyWal">True if only WAL file should be flushed, not memtable.</param>
    void Flush(bool onlyWal);
    void Compact();

    public enum KeyScheme
    {
        Hash,
        HalfPath,

        // The default setting in config, which for some reason, can't be a null enum.
        Current,
    }

    public interface IWriteBatch : IDisposable
    {
        void Set(Hash256? address, in TreePath path, in ValueHash256 currentNodeKeccak, ReadOnlySpan<byte> data, WriteFlags writeFlags);
    }
}

/// <summary>
/// Provides short-lived read-only snapshots for node storage implementations that can share one stable read view.
/// </summary>
public interface INodeStorageWithReadSnapshot
{
    /// <summary>
    /// Creates a read-only node storage snapshot, or returns <see langword="null" /> when the backing store cannot snapshot.
    /// </summary>
    /// <returns>A read-only snapshot, or <see langword="null" /> when snapshotting is unavailable.</returns>
    INodeStorageReadSnapshot? CreateReadSnapshot();
}

/// <summary>
/// Read-only node storage view that must be disposed to release the underlying snapshot resources.
/// </summary>
public interface INodeStorageReadSnapshot : IReadOnlyNodeStorage, IDisposable
{
}
