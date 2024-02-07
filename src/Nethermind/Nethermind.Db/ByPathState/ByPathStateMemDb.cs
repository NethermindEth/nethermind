// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Logging;

namespace Nethermind.Db.ByPathState;
public class ByPathStateMemDb : MemColumnsDb<StateColumns>, IByPathStateDb
{
    private Dictionary<StateColumns, ByPathStateDbPrunner> _prunners;

    public ByPathStateMemDb(ILogManager logManager) : base()
    {
        _prunners = new Dictionary<StateColumns, ByPathStateDbPrunner>()
        {
            { StateColumns.State, new ByPathStateDbPrunner(StateColumns.State, GetColumnDb(StateColumns.State), logManager)},
            { StateColumns.Storage, new ByPathStateDbPrunner(StateColumns.Storage, GetColumnDb(StateColumns.Storage), logManager)}
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
}
