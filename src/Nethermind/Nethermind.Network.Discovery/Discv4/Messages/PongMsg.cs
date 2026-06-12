// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.Discv4.Messages;

public sealed class PongMsg : DiscoveryMsg
{
    public ValueHash256 PingMdc { get; init; }

    public ulong? EnrSequence { get; set; }

    public PongMsg(IPEndPoint farAddress, long expirationTime, ValueHash256 pingMdc, ulong? enrSequence = null) : base(farAddress, expirationTime)
    {
        PingMdc = pingMdc;
        EnrSequence = enrSequence;
    }

    public PongMsg(PublicKey farPublicKey, long expirationTime, ValueHash256 pingMdc, ulong? enrSequence = null) : base(farPublicKey, expirationTime)
    {
        PingMdc = pingMdc;
        EnrSequence = enrSequence;
    }

    public override string ToString() => base.ToString() + $", PingMdc: {PingMdc}";

    public override MsgType MsgType => MsgType.Pong;
}
