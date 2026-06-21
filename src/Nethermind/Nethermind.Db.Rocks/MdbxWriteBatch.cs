// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Db.Rocks;

internal sealed class MdbxWriteBatch(MdbxEnvironment environment, uint dbi, IMergeOperator? mergeOperator, List<MdbxWriteOperation> operations) : IWriteBatch
{
    private readonly MdbxEnvironment _environment = environment;
    private readonly uint _dbi = dbi;
    private readonly IMergeOperator? _mergeOperator = mergeOperator;
    private readonly List<MdbxWriteOperation> _operations = operations;
    private bool _disposed;

    public MdbxWriteBatch(MdbxEnvironment environment, uint dbi, IMergeOperator? mergeOperator)
        : this(environment, dbi, mergeOperator, [])
    {
    }

    public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) =>
        throw new NotSupportedException("Write batches are write-only.");

    public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _operations.Add(MdbxWriteOperation.Set(_dbi, key, value, _mergeOperator));
    }

    public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _operations.Add(MdbxWriteOperation.PutSpan(_dbi, key, value, _mergeOperator));
    }

    public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _operations.Add(MdbxWriteOperation.Merge(_dbi, key, value, _mergeOperator));
    }

    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _operations.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _environment.ApplyBatch(_operations);
    }
}

internal sealed class MdbxColumnsWriteBatch<TKey>(
    MdbxEnvironment environment,
    IReadOnlyDictionary<TKey, ColumnDb> columns) : IColumnsWriteBatch<TKey> where TKey : notnull
{
    private readonly MdbxEnvironment _environment = environment;
    private readonly IReadOnlyDictionary<TKey, ColumnDb> _columns = columns;
    private readonly List<MdbxWriteOperation> _operations = [];
    private bool _disposed;

    public IWriteBatch GetColumnBatch(TKey key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ColumnDb column = _columns[key];
        return new MdbxColumnWriteBatch(this, column.Dbi, column.MergeOperator, _operations);
    }

    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _operations.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _environment.ApplyBatch(_operations);
    }

    private sealed class MdbxColumnWriteBatch(
        MdbxColumnsWriteBatch<TKey> owner,
        uint dbi,
        IMergeOperator? mergeOperator,
        List<MdbxWriteOperation> operations) : IWriteBatch
    {
        private readonly MdbxColumnsWriteBatch<TKey> _owner = owner;
        private readonly uint _dbi = dbi;
        private readonly IMergeOperator? _mergeOperator = mergeOperator;
        private readonly List<MdbxWriteOperation> _operations = operations;

        public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) =>
            throw new NotSupportedException("Write batches are write-only.");

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            ObjectDisposedException.ThrowIf(_owner._disposed, _owner);
            _operations.Add(MdbxWriteOperation.Set(_dbi, key, value, _mergeOperator));
        }

        public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
        {
            ObjectDisposedException.ThrowIf(_owner._disposed, _owner);
            _operations.Add(MdbxWriteOperation.PutSpan(_dbi, key, value, _mergeOperator));
        }

        public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
        {
            ObjectDisposedException.ThrowIf(_owner._disposed, _owner);
            _operations.Add(MdbxWriteOperation.Merge(_dbi, key, value, _mergeOperator));
        }

        public void Clear()
        {
            ObjectDisposedException.ThrowIf(_owner._disposed, _owner);
            int writeIndex = 0;
            for (int readIndex = 0; readIndex < _operations.Count; readIndex++)
            {
                MdbxWriteOperation operation = _operations[readIndex];
                if (operation.Dbi != _dbi)
                {
                    _operations[writeIndex++] = operation;
                }
            }

            if (writeIndex != _operations.Count)
            {
                _operations.RemoveRange(writeIndex, _operations.Count - writeIndex);
            }
        }

        public void Dispose()
        {
        }
    }
}
