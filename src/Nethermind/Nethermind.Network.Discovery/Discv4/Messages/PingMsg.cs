// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.Discv4.Messages;

public sealed class PingMsg : DiscoveryMsg
{
    public IPEndPoint SourceAddress { get; }
    public IPEndPoint DestinationAddress { get; }
    public int SourceTcpPort { get; }
    public int DestinationTcpPort { get; }

    /// <summary>
    /// Modification detection code
    /// </summary>
    public ValueHash256? Mdc { get; set; }

    /// <summary>
    /// https://eips.ethereum.org/EIPS/eip-868
    /// </summary>
    public ulong? EnrSequence { get; set; }

    public PingMsg(
        PublicKey farPublicKey,
        long expirationTime,
        IPEndPoint source,
        IPEndPoint destination,
        byte[] mdc,
        int? sourceTcpPort = null,
        int? destinationTcpPort = null)
        : this(farPublicKey, expirationTime, source, destination, CreateHash(mdc), sourceTcpPort, destinationTcpPort)
    {
    }

    public PingMsg(
        PublicKey farPublicKey,
        long expirationTime,
        IPEndPoint source,
        IPEndPoint destination,
        ValueHash256 mdc,
        int? sourceTcpPort = null,
        int? destinationTcpPort = null)
        : base(farPublicKey, expirationTime)
    {
        SourceAddress = source ?? throw new ArgumentNullException(nameof(source));
        DestinationAddress = destination ?? throw new ArgumentNullException(nameof(destination));
        SourceTcpPort = GetTcpPort(sourceTcpPort, SourceAddress.Port, nameof(sourceTcpPort));
        DestinationTcpPort = GetTcpPort(destinationTcpPort, DestinationAddress.Port, nameof(destinationTcpPort));
        Mdc = mdc;
    }

    public PingMsg(IPEndPoint farAddress, long expirationTime, IPEndPoint sourceAddress)
        : this(farAddress, expirationTime, sourceAddress, sourceAddress?.Port ?? 0, 0)
    {
    }

    public PingMsg(IPEndPoint farAddress, long expirationTime, IPEndPoint sourceAddress, int sourceTcpPort, int destinationTcpPort)
        : base(farAddress, expirationTime)
    {
        SourceAddress = sourceAddress ?? throw new ArgumentNullException(nameof(sourceAddress));
        DestinationAddress = farAddress;
        SourceTcpPort = GetTcpPort(sourceTcpPort, nameof(sourceTcpPort));
        DestinationTcpPort = GetTcpPort(destinationTcpPort, nameof(destinationTcpPort));
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

    private static int GetTcpPort(int? port, int fallbackPort, string paramName)
        => port is { } value ? GetTcpPort(value, paramName) : fallbackPort;

    private static int GetTcpPort(int port, string paramName)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(port, paramName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, ushort.MaxValue, paramName);
        return port;
    }
}
