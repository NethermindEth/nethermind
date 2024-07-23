// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.Verkle;

public static class VerkleMessageCode
{
    public const int GetSubTreeRange = 0x00;
    public const int SubTreeRange = 0x01;
    public const int GetLeafNodes = 0x02;
    public const int LeafNodes = 0x03;
}
