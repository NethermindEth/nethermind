// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Db.Rocks;

internal sealed class MdbxKeyValueStoreSnapshot(MdbxEnvironment environment, uint dbi) : IKeyValueStoreSnapshot, ISortedKeyValueStore
{
    private readonly MdbxEnvironment _environment = environment;
    private readonly MdbxNative.SafeMdbxTxnHandle _txn = environment.BeginReadOnlyTransaction();
    private readonly uint _dbi = dbi;
    private readonly object _lifetimeLock = new();
    private int _activeViews;
    private bool _disposed;
    private bool _transactionDisposed;

    public byte[]? FirstKey
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return MdbxCursorHelpers.GetEdge(_txn, _dbi, MdbxCursorOp.First);
        }
    }

    public byte[]? LastKey
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return MdbxCursorHelpers.GetEdge(_txn, _dbi, MdbxCursorOp.Last);
        }
    }

    public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _environment.Get(_txn, _dbi, key);
    }

    public Span<byte> GetSpan(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) =>
        Get(key, flags);

    public bool KeyExists(ReadOnlySpan<byte> key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _environment.KeyExists(_txn, _dbi, key);
    }

    public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive)
    {
        lock (_lifetimeLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeViews++;
        }

        try
        {
            return new SnapshotSortedView(
                this,
                new MdbxSortedView(_environment, _txn, _dbi, firstKeyInclusive, lastKeyExclusive, ownsTransaction: false));
        }
        catch
        {
            ReleaseView();
            throw;
        }
    }

    public void Dispose()
    {
        lock (_lifetimeLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposeTransactionIfUnused();
        }
    }

    private void ReleaseView()
    {
        if (_disposed)
        {
            lock (_lifetimeLock)
            {
                _activeViews--;
                DisposeTransactionIfUnused();
            }

            return;
        }

        lock (_lifetimeLock)
        {
            _activeViews--;
            DisposeTransactionIfUnused();
        }
    }

    private void DisposeTransactionIfUnused()
    {
        if (_transactionDisposed || !_disposed || _activeViews != 0)
        {
            return;
        }

        _transactionDisposed = true;
        _txn.Dispose();
    }

    private sealed class SnapshotSortedView(MdbxKeyValueStoreSnapshot owner, ISortedView inner) : ISortedView
    {
        private readonly MdbxKeyValueStoreSnapshot _owner = owner;
        private readonly ISortedView _inner = inner;
        private bool _disposed;

        public ReadOnlySpan<byte> CurrentKey
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _inner.CurrentKey;
            }
        }

        public ReadOnlySpan<byte> CurrentValue
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _inner.CurrentValue;
            }
        }

        public bool StartBefore(ReadOnlySpan<byte> value)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _inner.StartBefore(value);
        }

        public bool MoveNext()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _inner.MoveNext();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _inner.Dispose();
            _owner.ReleaseView();
        }
    }
}

internal sealed class MdbxColumnDbSnapshot<TKey>(
    MdbxEnvironment environment,
    IReadOnlyDictionary<TKey, ColumnDb> columns) : IColumnDbSnapshot<TKey> where TKey : notnull
{
    private readonly SnapshotState _state = CreateState(environment, columns);
    private bool _disposed;

    public IReadOnlyKeyValueStore GetColumn(TKey key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _state.Columns[key];
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _state.Transaction.Dispose();
    }

    private static SnapshotState CreateState(
        MdbxEnvironment environment,
        IReadOnlyDictionary<TKey, ColumnDb> columns)
    {
        MdbxNative.SafeMdbxTxnHandle txn = environment.BeginReadOnlyTransaction();
        Dictionary<TKey, SnapshotColumn> result = [];
        foreach (KeyValuePair<TKey, ColumnDb> column in columns)
        {
            result[column.Key] = new SnapshotColumn(environment, txn, column.Value.Dbi);
        }

        return new SnapshotState(txn, result);
    }

    private readonly record struct SnapshotState(MdbxNative.SafeMdbxTxnHandle Transaction, Dictionary<TKey, SnapshotColumn> Columns);

    private sealed class SnapshotColumn(
        MdbxEnvironment environment,
        MdbxNative.SafeMdbxTxnHandle txn,
        uint dbi) : IReadOnlyKeyValueStore
    {
        private readonly MdbxEnvironment _environment = environment;
        private readonly MdbxNative.SafeMdbxTxnHandle _txn = txn;
        private readonly uint _dbi = dbi;

        public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) =>
            _environment.Get(_txn, _dbi, key);

        public Span<byte> GetSpan(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) =>
            Get(key, flags);

        public bool KeyExists(ReadOnlySpan<byte> key) =>
            _environment.KeyExists(_txn, _dbi, key);
    }
}
