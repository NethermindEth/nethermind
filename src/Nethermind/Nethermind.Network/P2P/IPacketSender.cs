// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P
{
    public interface IPacketSender
    {
        int Enqueue<T>(T message) where T : P2PMessage;
    }
}
