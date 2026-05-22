// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Network.Enr;

namespace Nethermind.Network.Discovery.Discv5;

internal enum Discv5MessageType : byte
{
    Ping = 0x01,
    Pong = 0x02,
    FindNode = 0x03,
    Nodes = 0x04,
    TalkReq = 0x05,
    TalkResp = 0x06
}

internal abstract record Discv5Message(byte[] RequestId)
{
    public abstract Discv5MessageType MessageType { get; }
}

internal sealed record Discv5Ping(byte[] RequestId, ulong EnrSequence) : Discv5Message(RequestId)
{
    public override Discv5MessageType MessageType => Discv5MessageType.Ping;
}

internal sealed record Discv5Pong(byte[] RequestId, ulong EnrSequence, IPAddress RecipientIp, int RecipientPort) : Discv5Message(RequestId)
{
    public override Discv5MessageType MessageType => Discv5MessageType.Pong;
}

internal sealed record Discv5FindNode(byte[] RequestId, int[] Distances) : Discv5Message(RequestId)
{
    public override Discv5MessageType MessageType => Discv5MessageType.FindNode;
}

internal sealed record Discv5Nodes(byte[] RequestId, int Total, NodeRecord[] Records) : Discv5Message(RequestId)
{
    public override Discv5MessageType MessageType => Discv5MessageType.Nodes;
}

internal sealed record Discv5TalkReq(byte[] RequestId, byte[] Protocol, byte[] Request) : Discv5Message(RequestId)
{
    public override Discv5MessageType MessageType => Discv5MessageType.TalkReq;
}

internal sealed record Discv5TalkResp(byte[] RequestId, byte[] Response) : Discv5Message(RequestId)
{
    public override Discv5MessageType MessageType => Discv5MessageType.TalkResp;
}
