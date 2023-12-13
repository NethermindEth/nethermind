// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Core.Crypto;
using Nethermind.Network.Enr;

namespace Nethermind.Network.Discovery.Messages;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-868
/// </summary>
public class EnrResponseMsg : DiscoveryMsg
{
    private const long MaxTime = long.MaxValue; // non-expiring message

    public override MsgType MsgType => MsgType.EnrResponse;

    public NodeRecord NodeRecord { get; }

    public Hash256 RequestKeccak { get; set; }

    public EnrResponseMsg(IPEndPoint farAddress, NodeRecord nodeRecord, Hash256 requestKeccak)
        : base(farAddress, MaxTime)
    {
        NodeRecord = nodeRecord;
        RequestKeccak = requestKeccak;
    }

    public EnrResponseMsg(PublicKey farPublicKey, NodeRecord nodeRecord, Hash256 requestKeccak)
        : base(farPublicKey, MaxTime)
    {
        NodeRecord = nodeRecord;
        RequestKeccak = requestKeccak;
    }
}
