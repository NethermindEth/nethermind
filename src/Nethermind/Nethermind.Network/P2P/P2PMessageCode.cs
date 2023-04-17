// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P
{
    public static class P2PMessageCode
    {
        public const int Hello = 0x00;
        public const int Disconnect = 0x01;
        public const int Ping = 0x02;
        public const int Pong = 0x03;
        public const int AddCapability = 0x04;
    }
}
