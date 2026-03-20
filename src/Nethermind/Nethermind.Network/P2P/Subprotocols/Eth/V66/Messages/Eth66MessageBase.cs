// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    /// <summary>
    /// Base type for eth request/response messages that carry a request id on the wire.
    /// </summary>
    public abstract class Eth66MessageBase : P2PMessage, IEth66Message
    {
        public long RequestId { get; set; }

        protected Eth66MessageBase(bool generateRandomRequestId = true)
        {
            RequestId = generateRandomRequestId ? MessageConstants.Random.NextLong() : 0;
        }
    }
}
