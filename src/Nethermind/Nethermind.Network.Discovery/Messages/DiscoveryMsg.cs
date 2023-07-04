// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.Messages;

public abstract class DiscoveryMsg : MessageBase
{
    /// <summary>
    /// For incoming messages far address is set after deserialization
    /// </summary>
    public IPEndPoint? FarAddress { get; set; }

    public PublicKey? FarPublicKey { get; init; }

    public int Version { get; set; } = 4;

    protected DiscoveryMsg(IPEndPoint farAddress, long expirationTime)
    {
        FarAddress = farAddress ?? throw new ArgumentNullException(nameof(farAddress));
        ExpirationTime = expirationTime;
    }

    protected DiscoveryMsg(PublicKey? farPublicKey, long expirationTime)
    {
        FarPublicKey = farPublicKey; // if it is null then it suggests that the signature is not correct
        ExpirationTime = expirationTime;
    }

    /// <summary>
    /// Message expiry time as Unix epoch seconds
    /// </summary>
    public long ExpirationTime { get; init; }

    public override string ToString()
    {
        return $"Type: {MsgType}, FarAddress: {FarAddress?.ToString() ?? "empty"}, FarPublicKey: {FarPublicKey?.ToString() ?? "empty"}, ExpirationTime: {ExpirationTime}";
    }

    public abstract MsgType MsgType { get; }
}
