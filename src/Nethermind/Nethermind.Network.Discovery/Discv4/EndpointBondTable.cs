// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;

namespace Nethermind.Network.Discovery.Discv4;

internal readonly record struct EndpointKey(IPAddress Address, int Port)
{
    public EndpointKey(IPEndPoint endpoint)
        : this(endpoint.Address, endpoint.Port)
    {
    }
}

// Bounded per-session endpoint bond state used for received proofs and pending bonding pings.
internal struct EndpointBondTable
{
    public const int Capacity = 16;

    private Entry[]? _entries;
    private int _count;

    public readonly int Count => _count;
    public readonly bool IsEmpty => _count == 0;

    public bool Record(EndpointKey endpoint, long stamp)
    {
        Entry[] entries = _entries ??= new Entry[Capacity];
        for (int i = 0; i < _count; i++)
        {
            if (entries[i].Endpoint.Equals(endpoint))
            {
                entries[i] = new(endpoint, stamp);
                return false;
            }
        }

        if (_count < entries.Length)
        {
            entries[_count++] = new(endpoint, stamp);
            return false;
        }

        entries[LowestStampIndex(entries)] = new(endpoint, stamp);
        return true;
    }

    public bool Remove(EndpointKey endpoint, long expectedStamp)
    {
        Entry[]? entries = _entries;
        if (entries is null) return false;

        for (int i = 0; i < _count; i++)
        {
            if (entries[i].Endpoint.Equals(endpoint) && entries[i].Stamp == expectedStamp)
            {
                RemoveAt(entries, i);
                return true;
            }
        }

        return false;
    }

    public readonly bool Contains(EndpointKey endpoint)
    {
        Entry[]? entries = _entries;
        if (entries is null) return false;

        for (int i = 0; i < _count; i++)
        {
            if (entries[i].Endpoint.Equals(endpoint))
            {
                return true;
            }
        }

        return false;
    }

    public readonly bool HasFresh(EndpointKey endpoint, long minValidStamp)
    {
        Entry[]? entries = _entries;
        if (entries is null) return false;

        for (int i = 0; i < _count; i++)
        {
            Entry entry = entries[i];
            if (entry.Endpoint.Equals(endpoint) && entry.Stamp > minValidStamp)
            {
                return true;
            }
        }

        return false;
    }

    public void PruneStale(long minValidStamp)
    {
        Entry[]? entries = _entries;
        if (entries is null) return;

        int i = 0;
        while (i < _count)
        {
            if (entries[i].Stamp > minValidStamp)
            {
                i++;
                continue;
            }

            RemoveAt(entries, i);
        }
    }

    private void RemoveAt(Entry[] entries, int index)
    {
        _count--;
        entries[index] = entries[_count];
        entries[_count] = default;
    }

    private static int LowestStampIndex(Entry[] entries)
    {
        int lowestIndex = 0;
        long lowestStamp = entries[0].Stamp;
        for (int i = 1; i < entries.Length; i++)
        {
            if (entries[i].Stamp >= lowestStamp) continue;

            lowestIndex = i;
            lowestStamp = entries[i].Stamp;
        }

        return lowestIndex;
    }

    private readonly record struct Entry(EndpointKey Endpoint, long Stamp);
}
