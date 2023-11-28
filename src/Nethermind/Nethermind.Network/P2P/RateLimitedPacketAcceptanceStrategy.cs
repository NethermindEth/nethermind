// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Network.Rlpx;

namespace Nethermind.Network.P2P;

public class RateLimitedPacketAcceptanceStrategy : IPacketAcceptanceStrategy
{
    private readonly int _byteLimit;
    private readonly TimeSpan _throttle;

    private int _bytesAccepted;
    private DateTime _timestamp;

    public RateLimitedPacketAcceptanceStrategy(int byteLimit, TimeSpan throttle)
    {
        _byteLimit = byteLimit;
        _throttle = throttle;
        _timestamp = DateTime.UnixEpoch;
    }
    public bool Accepts(ZeroPacket packet)
    {
        int bytesToAccept = packet.Content.ReadableBytes;
        DateTime now = DateTime.UtcNow;

        if (_timestamp.Add(_throttle) <= now)
        {
            _timestamp = now;
            _bytesAccepted = bytesToAccept;
            return true;
        }

        if (_bytesAccepted + bytesToAccept <= _byteLimit)
        {
            _bytesAccepted += bytesToAccept;
            return true;
        }

        return false;
    }
}
