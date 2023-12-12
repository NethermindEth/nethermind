// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Rlpx;

namespace Nethermind.Network.P2P;

public class ConcurrentPacketAcceptanceStrategy : IPacketAcceptanceStrategy
{
    private readonly IPacketAcceptanceStrategy _strategy;
    private readonly object _lock;

    public ConcurrentPacketAcceptanceStrategy(IPacketAcceptanceStrategy strategy)
    {
        _strategy = strategy;
        _lock = new object();
    }

    public bool Accepts(ZeroPacket packet)
    {
        lock (_lock)
        {
            return _strategy.Accepts(packet);
        }
    }
}
