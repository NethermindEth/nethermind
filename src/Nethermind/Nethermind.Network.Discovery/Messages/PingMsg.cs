// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Network.Discovery.Messages;

public class PingMsg : DiscoveryMsg
{
    public IPEndPoint SourceAddress { get; }
    public IPEndPoint DestinationAddress { get; }

    /// <summary>
    /// Modification detection code
    /// </summary>
    public byte[]? Mdc { get; set; }

    /// <summary>
    /// https://eips.ethereum.org/EIPS/eip-868
    /// </summary>
    public long? EnrSequence { get; set; }

    public PingMsg(PublicKey farPublicKey, long expirationTime, IPEndPoint source, IPEndPoint destination, byte[] mdc)
        : base(farPublicKey, expirationTime)
    {
        SourceAddress = source ?? throw new ArgumentNullException(nameof(source));
        DestinationAddress = destination ?? throw new ArgumentNullException(nameof(destination));
        Mdc = mdc ?? throw new ArgumentNullException(nameof(mdc));
    }

    public PingMsg(IPEndPoint farAddress, long expirationTime, IPEndPoint sourceAddress)
        : base(farAddress, expirationTime)
    {
        SourceAddress = sourceAddress ?? throw new ArgumentNullException(nameof(sourceAddress));
        DestinationAddress = farAddress;
    }

    public override string ToString()
    {
        return base.ToString() + $", SourceAddress: {SourceAddress}, DestinationAddress: {DestinationAddress}, Version: {Version}, Mdc: {Mdc?.ToHexString()}";
    }

    public override MsgType MsgType => MsgType.Ping;
}
