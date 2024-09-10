// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery;

public class UnackedItem(UTPPacketHeader header, ArraySegment<byte> buffer, uint sentTime)
{
    public UTPPacketHeader Header => header;
    public bool AssumedLoss { get; set; }
    public int UnackedCounter { get; set; }
    public int TransmitCount { get; set; }
    public uint SentTime { get; set; } = sentTime;
    public bool NeedResent { get; set; }
    public ArraySegment<byte> Buffer => buffer;
}
