// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Discv5.Packets;

internal enum PacketFlag : byte
{
    Ordinary = 0,
    WhoAreYou = 1,
    Handshake = 2
}
