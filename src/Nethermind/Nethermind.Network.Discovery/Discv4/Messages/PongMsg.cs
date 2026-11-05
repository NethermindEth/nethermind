// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.Discv4.Messages;

public sealed class PongMsg : DiscoveryMsg
{
    public ValueHash256 PingMdc { get; init; }

    /// <summary>
    /// https://eips.ethereum.org/EIPS/eip-868
    /// </summary>
    public ulong? EnrSequence { get; }

    public PongMsg(IPEndPoint farAddress, long expirationTime, ValueHash256 pingMdc)
        : this(farAddress, expirationTime, pingMdc, null)
    {
    }

    public PongMsg(IPEndPoint farAddress, long expirationTime, ValueHash256 pingMdc, ulong? enrSequence)
        : base(farAddress, expirationTime)
    {
        PingMdc = pingMdc;
        EnrSequence = enrSequence;
    }

    public PongMsg(PublicKey farPublicKey, long expirationTime, ValueHash256 pingMdc)
        : this(farPublicKey, expirationTime, pingMdc, null)
    {
    }

    public PongMsg(PublicKey farPublicKey, long expirationTime, ValueHash256 pingMdc, ulong? enrSequence)
        : base(farPublicKey, expirationTime)
    {
        PingMdc = pingMdc;
        EnrSequence = enrSequence;
    }

    public override string ToString() => base.ToString() + $", PingMdc: {PingMdc}, EnrSequence: {EnrSequence}";

    public override MsgType MsgType => MsgType.Pong;
}
