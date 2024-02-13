// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Verkle.Messages;

public abstract class VerkleMessageBase : P2PMessage
{
    public override string Protocol => Contract.P2P.Protocol.Verkle;
    /// <summary>
    /// Request ID to match up responses with
    /// </summary>
    public long RequestId { get; set; }

    protected VerkleMessageBase(bool generateRandomRequestId = true)
    {
        if (generateRandomRequestId)
        {
            RequestId = MessageConstants.Random.NextLong();
        }
    }
}
