// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Rlpx;

namespace Nethermind.Network.P2P.ProtocolHandlers
{
    public interface IZeroProtocolHandler : IProtocolHandler
    {
        void HandleMessage(ZeroPacket message);
    }
}
