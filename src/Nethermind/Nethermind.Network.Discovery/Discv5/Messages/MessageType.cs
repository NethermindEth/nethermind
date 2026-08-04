// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Discv5.Messages;

internal enum MessageType : byte
{
    Ping = 0x01,
    Pong = 0x02,
    FindNode = 0x03,
    Nodes = 0x04,
    TalkReq = 0x05,
    TalkResp = 0x06
}
