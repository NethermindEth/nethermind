// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Db.ByPathState;
public class ByPathStateDb : IByPathStateDb
{
    private readonly RocksDbSettings _settings;
    private readonly IRocksDbFactory _dbFactory;

    private IColumnsDb<StateColumns> _currentDb;
    private Dictionary<StateColumns, ByPathStateDbPrunner> _prunners;

    public ByPathStateDb(RocksDbSettings settings, IRocksDbFactory dbFactory)
    {
        _settings = settings;
        _dbFactory = dbFactory;
        _currentDb = _dbFactory.CreateColumnsDb<StateColumns>(_settings);
        _prunners = new Dictionary<StateColumns, ByPathStateDbPrunner>()
        {
            { StateColumns.State, new ByPathStateDbPrunner(_currentDb.GetColumnDb(StateColumns.State), LimboLogs.Instance)},
            { StateColumns.Storage, new ByPathStateDbPrunner(_currentDb.GetColumnDb(StateColumns.State), LimboLogs.Instance)}
        };
    }

    public bool CanAccessByPath(StateColumns column)
    {
        return _prunners[column].IsPruningComplete;
    }

    public void EnqueueDeleteRange(StateColumns column, Span<byte> from, Span<byte> to)
    {
        _prunners[column].EnqueueRange(from, to);
    }

    public void StartPrunning()
    {
        _prunners[StateColumns.State].Start();
        _prunners[StateColumns.Storage].Start();
    }

    public void WaitForPrunning()
    {
        _prunners[StateColumns.State].Wait();
        _prunners[StateColumns.Storage].Wait();
    }

    public void EndOfCleanupRequests()
    {
        _prunners[StateColumns.State].EndOfCleanupRequests();
        _prunners[StateColumns.Storage].EndOfCleanupRequests();
    }

    #region IColumnsDb<StateColumns>

    public KeyValuePair<byte[], byte[]?>[] this[byte[][] keys] => throw new NotImplementedException();

    public IEnumerable<StateColumns> ColumnKeys => throw new NotImplementedException();

    public string Name => _currentDb.Name;

    public void Clear()
    {
        _currentDb.Clear();
    }

    public void DangerousReleaseMemory(in Span<byte> span)
    {
        _currentDb.DangerousReleaseMemory(span);
    }

    public void DeleteByRange(Span<byte> startKey, Span<byte> endKey)
    {
        _currentDb.DeleteByRange(startKey, endKey);
    }

    public void Dispose()
    {
        _currentDb?.Dispose();
    }

    public void Flush()
    {
        _currentDb.Flush();
    }

    public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
    {
        return _currentDb.Get(key, flags);
    }

    public IEnumerable<KeyValuePair<byte[], byte[]?>> GetAll(bool ordered = false)
    {
        return _currentDb.GetAll(ordered);
    }

    public IEnumerable<byte[]> GetAllValues(bool ordered = false)
    {
        return _currentDb.GetAllValues(ordered);
    }

    public long GetCacheSize()
    {
        return _currentDb.GetCacheSize();
    }

    public IDbWithSpan GetColumnDb(StateColumns key)
    {
        return _currentDb.GetColumnDb(key);
    }

    public long GetIndexSize()
    {
        return _currentDb.GetIndexSize();
    }

    public long GetMemtableSize()
    {
        return _currentDb.GetMemtableSize();
    }

    public long GetSize()
    {
        return _currentDb.GetSize();
    }

    public Span<byte> GetSpan(ReadOnlySpan<byte> key)
    {
        return _currentDb.GetSpan(key);
    }

    public bool KeyExists(ReadOnlySpan<byte> key)
    {
        return _currentDb.KeyExists(key);
    }

    public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        _currentDb.PutSpan(key, value);
    }

    public void Remove(ReadOnlySpan<byte> key)
    {
        _currentDb.Remove(key);
    }

    public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
    {
        _currentDb.Set(key, value, flags);
    }

    public IBatch StartBatch()
    {
        return _currentDb.StartBatch();
    }
    #endregion
}

public interface IByPathStateDb : IColumnsDb<StateColumns>
{
    IDbWithSpan GetStateDb() => GetColumnDb(StateColumns.State);
    IDbWithSpan GetStorageDb() => GetColumnDb(StateColumns.Storage);

    bool CanAccessByPath(StateColumns column);
    void EnqueueDeleteRange(StateColumns column, Span<byte> from, Span<byte> to);

    void StartPrunning();
    void WaitForPrunning();
    void EndOfCleanupRequests();
}
