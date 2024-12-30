// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NonBlocking;

namespace Nethermind.Network.Portal.UTP;

public class ReceiveBuffer
{
    private readonly ConcurrentDictionary<ushort, ArraySegment<byte>?> _receiveBuffer = new();
    private int _bufferSize = 0;
    public int Size => _bufferSize;

    public bool TryRemove(ushort seqNumber, out ArraySegment<byte>? arraySegment)
    {
        var ok = _receiveBuffer.TryRemove(seqNumber, out arraySegment);
        if (ok)
            Interlocked.Add(ref _bufferSize, -arraySegment?.Count ?? 0);

        return ok;
    }

    public byte[]? CompileSelectiveAckBitset(ushort curAck)
    {
        if (_receiveBuffer.Count == 0)
            return null;

        // Fixed 64 bit.
        // TODO: use long
        // TODO: no need to encode trailing zeros
        var selectiveAck = new byte[8];

        // Shortcut the loop if all buffer was iterated
        var counted = 0;
        var maxCounted = _receiveBuffer.Count;

        for (var i = 0; i < 64 && counted < maxCounted; i++)
        {
            var theAck = (ushort)(curAck + 2 + i);
            if (_receiveBuffer.ContainsKey(theAck))
            {
                var iIdx = i / 8;
                var iOffset = i % 8;
                selectiveAck[iIdx] = (byte)(selectiveAck[iIdx] | 1 << iOffset);
                counted++;
            }
        }

        return selectiveAck;
    }

    public IEnumerable<ushort> GetKeys()
    {
        return _receiveBuffer.Select(kv => kv.Key).ToList();
    }

    public bool ContainsKey(ushort seqNumber)
    {
        return _receiveBuffer.ContainsKey(seqNumber);
    }

    public bool TryAdd(ushort seqNumber, ArraySegment<byte>? segment)
    {
        var ok = _receiveBuffer.TryAdd(seqNumber, segment);
        if (ok)
            Interlocked.Add(ref _bufferSize, segment?.Count ?? 0);

        return ok;
    }
}
