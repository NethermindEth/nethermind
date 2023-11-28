// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Rlpx;

namespace Nethermind.Network.P2P;

public interface IPacketAcceptanceStrategy
{
    public bool Accepts(ZeroPacket packet);
}
