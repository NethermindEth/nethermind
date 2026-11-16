// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;

namespace Nethermind.Runner.JsonRpc;

internal sealed class UnsentResponseCounter
{
    private readonly Lock _lock = new();
    private readonly Dictionary<HttpJsonRpcResponseSink, long> _reserved = [];
    private long _bytes;

    public bool TryReserve(HttpJsonRpcResponseSink response, long bytes, long limit)
    {
        List<HttpJsonRpcResponseSink>? evicted = null;
        lock (_lock)
        {
            _reserved.TryGetValue(response, out long reserved);
            while (limit > 0 && _bytes + bytes > limit)
            {
                (HttpJsonRpcResponseSink? largest, long largestBytes) = FindLargest(response);
                if (largest is null || largestBytes <= reserved + bytes)
                {
                    return false;
                }

                _reserved.Remove(largest);
                _bytes -= largestBytes;
                (evicted ??= []).Add(largest);
            }

            _reserved[response] = reserved + bytes;
            _bytes += bytes;
        }

        if (evicted is not null)
        {
            foreach (HttpJsonRpcResponseSink sink in evicted)
            {
                sink.Evict();
            }
        }

        return true;
    }

    public void Release(HttpJsonRpcResponseSink response)
    {
        lock (_lock)
        {
            if (_reserved.Remove(response, out long reserved))
            {
                _bytes -= reserved;
            }
        }
    }

    private (HttpJsonRpcResponseSink? Sink, long Bytes) FindLargest(HttpJsonRpcResponseSink except)
    {
        HttpJsonRpcResponseSink? largest = null;
        long largestBytes = 0;
        foreach ((HttpJsonRpcResponseSink sink, long bytes) in _reserved)
        {
            if (bytes > largestBytes && !ReferenceEquals(sink, except))
            {
                largest = sink;
                largestBytes = bytes;
            }
        }

        return (largest, largestBytes);
    }
}
