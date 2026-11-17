// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.Tracing;

namespace Nethermind.Core.Test;

/// <summary>Records the buffers of at least <paramref name="minimumLength"/> bytes that this thread rents from <see cref="ArrayPool{T}.Shared"/> and does not return.</summary>
public sealed class SharedByteBufferRentals(int minimumLength) : EventListener
{
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly int _poolId = ArrayPool<byte>.Shared.GetHashCode();

    public HashSet<int> Outstanding { get; } = [];
    public int RentCount { get; private set; }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "System.Buffers.ArrayPoolEventSource") EnableEvents(eventSource, EventLevel.Verbose);
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (Environment.CurrentManagedThreadId != _threadId
            || eventData.Payload is not [int bufferId, int bufferSize, int poolId, ..]
            || poolId != _poolId
            || bufferSize < minimumLength)
        {
            return;
        }

        switch (eventData.EventName)
        {
            case "BufferRented":
                RentCount++;
                Outstanding.Add(bufferId);
                break;
            case "BufferReturned":
                Outstanding.Remove(bufferId);
                break;
        }
    }
}
