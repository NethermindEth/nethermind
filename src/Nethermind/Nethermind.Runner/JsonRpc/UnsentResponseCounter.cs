// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;

namespace Nethermind.Runner.JsonRpc;

internal sealed class UnsentResponseCounter
{
    internal const int ConnectionShareDivisor = 16;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, long> _connections = [];
    private long _bytes;

    public bool TryReserve(string connectionId, long bytes, long limit)
    {
        long connectionLimit = Math.Max(1, limit / ConnectionShareDivisor);
        lock (_lock)
        {
            _connections.TryGetValue(connectionId, out long connectionBytes);
            if (connectionBytes + bytes > connectionLimit || _bytes + bytes > limit)
            {
                return false;
            }

            _connections[connectionId] = connectionBytes + bytes;
            _bytes += bytes;
            return true;
        }
    }

    public void Release(string connectionId, long bytes)
    {
        lock (_lock)
        {
            if (!_connections.TryGetValue(connectionId, out long connectionBytes))
            {
                return;
            }

            long released = Math.Min(bytes, connectionBytes);
            if (connectionBytes == released)
            {
                _connections.Remove(connectionId);
            }
            else
            {
                _connections[connectionId] = connectionBytes - released;
            }

            _bytes -= released;
        }
    }
}
