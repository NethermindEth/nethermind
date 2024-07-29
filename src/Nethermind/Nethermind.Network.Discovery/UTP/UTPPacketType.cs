// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery;

public enum UTPPacketType: byte
{
    StData = 0,
    StFin = 1,
    StState = 2,
    StReset = 3,
    StSyn = 4,
}
