// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Core;

namespace Nethermind.Db.Rocks;

/// <summary>
/// MDBX-backed database view for a named column.
/// </summary>
public sealed class ColumnDb : IDb, IMergeableKeyValueStore, ISortedKeyValueStore, IKeyValueStoreWithSnapshot, IReadOnlyNativeKeyValueStore
{
    private readonly DbOnTheRocks _owner;

    internal ColumnDb(DbOnTheRocks owner, string name, uint dbi, IMergeOperator? mergeOperator)
    {
        _owner = owner;
        Name = name;
        Dbi = dbi;
        MergeOperator = mergeOperator;
    }

    public string Name { get; }

    internal uint Dbi { get; }

    internal IMergeOperator? MergeOperator { get; }

    public byte[]? FirstKey => _owner.Mdbx.ExecuteRead(txn => MdbxCursorHelpers.GetEdge(txn, Dbi, MdbxCursorOp.First));

    public byte[]? LastKey => _owner.Mdbx.ExecuteRead(txn => MdbxCursorHelpers.GetEdge(txn, Dbi, MdbxCursorOp.Last));

    public KeyValuePair<byte[], byte[]?>[] this[byte[][] keys]
    {
        get
        {
            KeyValuePair<byte[], byte[]?>[] result = new KeyValuePair<byte[], byte[]?>[keys.Length];
            _owner.Mdbx.ExecuteRead(txn =>
            {
                for (int i = 0; i < keys.Length; i++)
                {
                    byte[] key = keys[i];
                    result[i] = new KeyValuePair<byte[], byte[]?>(key, _owner.Mdbx.Get(txn, Dbi, key));
                }
            });

            return result;
        }
    }

    public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) =>
        _owner.Mdbx.Get(Dbi, key);

    public Span<byte> GetSpan(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) =>
        Get(key, flags);

    public bool KeyExists(ReadOnlySpan<byte> key) =>
        _owner.Mdbx.KeyExists(Dbi, key);

    public ReadOnlySpan<byte> GetNativeSlice(scoped ReadOnlySpan<byte> key, out IntPtr handle, ReadFlags flags = ReadFlags.None)
    {
        byte[]? data = Get(key, flags);
        if (data is null)
        {
            handle = IntPtr.Zero;
            return default;
        }

        handle = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, handle, data.Length);
        // The unmanaged copy is owned by the caller through DangerousReleaseHandle.
        unsafe
        {
            return new ReadOnlySpan<byte>((void*)handle, data.Length);
        }
    }

    public void DangerousReleaseHandle(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(handle);
        }
    }

    public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None) =>
        _owner.Mdbx.Put(Dbi, key, value);

    public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
    {
        byte[] keyCopy = key.ToArray();
        byte[] valueCopy = value.ToArray();
        _owner.Mdbx.ExecuteWrite(txn => _owner.Mdbx.Put(txn, Dbi, keyCopy, valueCopy));
    }

    public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) =>
        _owner.Mdbx.Merge(Dbi, key, value, MergeOperator);

    public IEnumerable<KeyValuePair<byte[], byte[]?>> GetAll(bool ordered = false) =>
        MdbxCursorHelpers.Enumerate(_owner.Mdbx, Dbi);

    public IEnumerable<byte[]> GetAllKeys(bool ordered = false)
    {
        foreach (KeyValuePair<byte[], byte[]?> item in GetAll(ordered))
        {
            yield return item.Key;
        }
    }

    public IEnumerable<byte[]> GetAllValues(bool ordered = false)
    {
        foreach (KeyValuePair<byte[], byte[]?> item in GetAll(ordered))
        {
            if (item.Value is not null)
            {
                yield return item.Value;
            }
        }
    }

    public IWriteBatch StartWriteBatch() =>
        _owner.CreateWriteBatch(Dbi, MergeOperator);

    public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive) =>
        new MdbxSortedView(_owner.Mdbx, Dbi, firstKeyInclusive, lastKeyExclusive);

    public IKeyValueStoreSnapshot CreateSnapshot() =>
        _owner.CreateSnapshot(Dbi);

    public IDb.DbMetric GatherMetric() =>
        _owner.GatherMetric();

    public void Flush(bool onlyWal = false) =>
        _owner.Flush(onlyWal);

    public void Clear() =>
        _owner.Mdbx.DropTable(Dbi);

    public void Compact() =>
        _owner.Compact();

    public void SetWriteBuffer(long sizeBytes)
    {
    }

    public void Dispose()
    {
    }
}
