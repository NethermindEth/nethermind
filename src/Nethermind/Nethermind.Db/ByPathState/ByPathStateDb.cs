// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Logging;

namespace Nethermind.Db.ByPathState;

public class ByPathStateDb : IByPathStateDb
{
    private readonly IColumnsDb<StateColumns> _currentDb;
    private readonly Dictionary<StateColumns, ByPathStateDbPrunner> _prunners;

    public ByPathStateDb(IColumnsDb<StateColumns> currentDb, ILogManager logManager)
    {
        _currentDb = currentDb;
        _prunners = new Dictionary<StateColumns, ByPathStateDbPrunner>()
        {
            { StateColumns.State, new ByPathStateDbPrunner(_currentDb.GetColumnDb(StateColumns.State), logManager)},
            { StateColumns.Storage, new ByPathStateDbPrunner(_currentDb.GetColumnDb(StateColumns.Storage), logManager)}
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

    public void Clear()
    {
        _currentDb.Clear();
    }

    public void Flush()
    {
        _currentDb.Flush();
    }

    public long GetCacheSize()
    {
        return _currentDb.GetCacheSize();
    }

    public IDb GetColumnDb(StateColumns key)
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

    public IColumnsWriteBatch<StateColumns> StartWriteBatch()
    {
        return _currentDb.StartWriteBatch();
    }
    #endregion
}

public interface IByPathStateDb : IColumnsDb<StateColumns>
{
    IDb GetStateDb() => GetColumnDb(StateColumns.State);
    IDb GetStorageDb() => GetColumnDb(StateColumns.Storage);

    bool CanAccessByPath(StateColumns column);
    void EnqueueDeleteRange(StateColumns column, Span<byte> from, Span<byte> to);

    void StartPrunning();
    void WaitForPrunning();
    void EndOfCleanupRequests();
}
