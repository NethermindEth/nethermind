// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Network.P2P.Messages
{
    public abstract class P2PMessage : MessageBase, IDisposable
    {
        public abstract int PacketType { get; }

        public int AdaptivePacketType { get; set; }

        public abstract string Protocol { get; }

        public virtual void Dispose()
        {
        }
    }
}
