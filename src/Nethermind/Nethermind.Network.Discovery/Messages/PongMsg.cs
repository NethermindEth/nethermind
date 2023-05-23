// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Network.Discovery.Messages;

public class PongMsg : DiscoveryMsg
{
    public byte[] PingMdc { get; init; }

    public PongMsg(IPEndPoint farAddress, long expirationTime, byte[] pingMdc) : base(farAddress, expirationTime)
    {
        PingMdc = pingMdc ?? throw new ArgumentNullException(nameof(pingMdc));
    }

    public PongMsg(PublicKey farPublicKey, long expirationTime, byte[] pingMdc) : base(farPublicKey, expirationTime)
    {
        PingMdc = pingMdc ?? throw new ArgumentNullException(nameof(pingMdc));
    }

    public override string ToString()
    {
        return base.ToString() + $", PingMdc: {PingMdc?.ToHexString() ?? "empty"}";
    }

    public override MsgType MsgType => MsgType.Pong;
}
