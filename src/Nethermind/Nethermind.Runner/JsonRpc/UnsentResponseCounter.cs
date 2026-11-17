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

    public bool TryReserve(HttpJsonRpcResponseSink response, long bytes, long limit, long stalledSendTimestamp)
    {
        List<HttpJsonRpcResponseSink>? evicted = null;
        bool reserved = false;
        lock (_lock)
        {
            _reserved.TryGetValue(response, out long reservedBytes);
            if (reservedBytes + bytes <= limit)
            {
                while (_bytes + bytes > limit)
                {
                    (HttpJsonRpcResponseSink? largest, long largestBytes) = FindLargestStalled(response, stalledSendTimestamp);
                    if (largest is null)
                    {
                        break;
                    }

                    _reserved.Remove(largest);
                    _bytes -= largestBytes;
                    (evicted ??= []).Add(largest);
                }

                if (_bytes + bytes <= limit)
                {
                    _reserved[response] = reservedBytes + bytes;
                    _bytes += bytes;
                    reserved = true;
                }
            }
        }

        if (evicted is not null)
        {
            foreach (HttpJsonRpcResponseSink sink in evicted)
            {
                sink.Evict();
            }
        }

        return reserved;
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

    private (HttpJsonRpcResponseSink? Sink, long Bytes) FindLargestStalled(HttpJsonRpcResponseSink except, long stalledSendTimestamp)
    {
        HttpJsonRpcResponseSink? largest = null;
        long largestBytes = 0;
        foreach ((HttpJsonRpcResponseSink sink, long bytes) in _reserved)
        {
            long sendStart = sink.SendStartTimestamp;
            if (bytes > largestBytes && sendStart != 0 && sendStart < stalledSendTimestamp && !ReferenceEquals(sink, except))
            {
                largest = sink;
                largestBytes = bytes;
            }
        }

        return (largest, largestBytes);
    }
}
