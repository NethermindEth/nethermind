// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Messages;

public enum MsgType
{
    Ping = 1,
    Pong = 2,
    FindNode = 3,
    Neighbors = 4,
    EnrRequest = 5,
    EnrResponse = 6
}
