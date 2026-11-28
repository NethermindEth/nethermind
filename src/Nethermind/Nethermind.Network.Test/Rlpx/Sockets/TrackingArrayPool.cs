// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Nethermind.Network.Test.Rlpx.Sockets;

/// <summary>
/// An array pool that tracks every rental, so a test can see buffers that were never returned or returned twice.
/// </summary>
internal sealed class TrackingArrayPool : ArrayPool<byte>
{
    private readonly Lock _lock = new();
    private readonly Dictionary<byte[], int> _outstanding = [with(ReferenceEqualityComparer.Instance)];
    private int _doubleReturns;

    /// <summary>Gets the lengths requested for the buffers not yet returned.</summary>
    public int[] Outstanding
    {
        get
        {
            lock (_lock)
            {
                return [.. _outstanding.Values.Order()];
            }
        }
    }

    /// <summary>Gets the lengths requested for the buffers not yet returned, apart from the send loop's batch.</summary>
    public int[] OutstandingExceptSendBatch => [.. Outstanding.Where(static length => length != Nethermind.Network.Rlpx.Sockets.SocketRlpxConnection.SendBatchSize)];

    public int DoubleReturns => Volatile.Read(ref _doubleReturns);

    public override byte[] Rent(int minimumLength)
    {
        byte[] buffer = new byte[minimumLength];
        lock (_lock)
        {
            _outstanding.Add(buffer, minimumLength);
        }

        return buffer;
    }

    public override void Return(byte[] array, bool clearArray = false)
    {
        lock (_lock)
        {
            if (!_outstanding.Remove(array))
            {
                _doubleReturns++;
            }
        }
    }
}
