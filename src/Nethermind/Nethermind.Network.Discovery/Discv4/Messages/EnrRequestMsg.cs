// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.Discv4.Messages;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-868
/// </summary>
public sealed class EnrRequestMsg : DiscoveryMsg
{
    public override MsgType MsgType => MsgType.EnrRequest;

    public ValueHash256? Hash { get; set; }

    public EnrRequestMsg(IPEndPoint farAddress, long expirationDate)
        : base(farAddress, expirationDate)
    {
    }

    public EnrRequestMsg(PublicKey farPublicKey, long expirationDate)
        : base(farPublicKey, expirationDate)
    {
    }

    internal EnrRequestMsg(PublicKey farPublicKey, ValueHash256 hash, long expirationDate)
        : base(farPublicKey, expirationDate) => Hash = hash;
}
