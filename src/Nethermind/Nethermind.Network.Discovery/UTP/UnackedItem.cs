// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery;

public class UnackedItem(UTPPacketHeader header, Memory<byte> buffer)
{
    public UTPPacketHeader Header => header;
    public bool AssumedLoss { get; set; }
    public int UnackedCounter { get; set; }
    public Memory<byte> Buffer => buffer;
}
