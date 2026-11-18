// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Kademlia;

internal sealed class DiscoveryRequestActivity
{
    private readonly Lock _lock = new();
    private long _generation;
    private bool _loadCompleted;
    private bool _online;

    internal void BeginLoad()
    {
        lock (_lock)
        {
            _generation++;
            _loadCompleted = false;
            _online = false;
        }
    }

    internal void CompleteLoad()
    {
        lock (_lock) _loadCompleted = true;
    }

    internal long BeginRequest()
    {
        lock (_lock) return _loadCompleted ? _generation : 0;
    }

    internal void Answered(long generation)
    {
        lock (_lock)
        {
            if (_loadCompleted && generation != 0 && generation == _generation) _online = true;
        }
    }

    internal bool IsOnline
    {
        get { lock (_lock) return _online; }
    }
}
