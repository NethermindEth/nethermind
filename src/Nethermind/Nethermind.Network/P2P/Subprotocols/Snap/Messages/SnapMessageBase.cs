// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public abstract class SnapMessageBase : P2PMessage, IEth66Message
    {
        public override string Protocol => Contract.P2P.Protocol.Snap;

        /// <summary>
        /// Request ID to match up responses with
        /// </summary>
        public long RequestId { get; set; } = MessageConstants.Random.NextLong();

        protected SnapMessageBase(bool generateRandomRequestId = true)
        {
            if (generateRandomRequestId)
            {
                RequestId = MessageConstants.Random.NextLong();
            }
        }
    }
}
