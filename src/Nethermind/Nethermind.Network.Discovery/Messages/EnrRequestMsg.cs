// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.Messages;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-868
/// </summary>
public class EnrRequestMsg : DiscoveryMsg
{
    public override MsgType MsgType => MsgType.EnrRequest;

    public Memory<byte>? Hash { get; }

    public EnrRequestMsg(IPEndPoint farAddress, long expirationDate)
        : base(farAddress, expirationDate)
    {
    }

    public EnrRequestMsg(PublicKey farPublicKey, long expirationDate)
        : base(farPublicKey, expirationDate)
    {
    }

    internal EnrRequestMsg(PublicKey farPublicKey, Memory<byte> hash, long expirationDate)
        : base(farPublicKey, expirationDate)
    {
        Hash = hash;
    }
}
