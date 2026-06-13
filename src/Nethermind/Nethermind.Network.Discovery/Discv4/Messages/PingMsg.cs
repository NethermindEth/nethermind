// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.Discv4.Messages;

public sealed class PingMsg : DiscoveryMsg
{
    public IPEndPoint SourceAddress { get; }
    public IPEndPoint DestinationAddress { get; }

    /// <summary>
    /// Modification detection code
    /// </summary>
    public ValueHash256? Mdc { get; set; }

    /// <summary>
    /// https://eips.ethereum.org/EIPS/eip-868
    /// </summary>
    public ulong? EnrSequence { get; set; }

    public PingMsg(PublicKey farPublicKey, long expirationTime, IPEndPoint source, IPEndPoint destination, byte[] mdc)
        : this(farPublicKey, expirationTime, source, destination, CreateHash(mdc))
    {
    }

    public PingMsg(PublicKey farPublicKey, long expirationTime, IPEndPoint source, IPEndPoint destination, ValueHash256 mdc)
        : base(farPublicKey, expirationTime)
    {
        SourceAddress = source ?? throw new ArgumentNullException(nameof(source));
        DestinationAddress = destination ?? throw new ArgumentNullException(nameof(destination));
        Mdc = mdc;
    }

    public PingMsg(IPEndPoint farAddress, long expirationTime, IPEndPoint sourceAddress)
        : base(farAddress, expirationTime)
    {
        SourceAddress = sourceAddress ?? throw new ArgumentNullException(nameof(sourceAddress));
        DestinationAddress = farAddress;
    }

    public override string ToString() => base.ToString() + $", SourceAddress: {SourceAddress}, DestinationAddress: {DestinationAddress}, Version: {Version}, Mdc: {(Mdc is { } mdc ? mdc.ToString() : null)}";

    public override MsgType MsgType => MsgType.Ping;

    private static ValueHash256 CreateHash(byte[] mdc)
    {
        ArgumentNullException.ThrowIfNull(mdc);
        if (mdc.Length != Hash256.Size)
        {
            throw new ArgumentException($"Discovery MDC must be {Hash256.Size} bytes.", nameof(mdc));
        }

        return new ValueHash256(mdc);
    }
}
