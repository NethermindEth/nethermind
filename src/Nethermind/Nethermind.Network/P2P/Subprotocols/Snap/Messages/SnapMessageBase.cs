// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Common.Utilities;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public abstract class SnapMessageBase : P2PMessage
    {
        public override string Protocol => Contract.P2P.Protocol.Snap;

        /// <summary>
        /// Request ID to match up responses with
        /// </summary>
        public long RequestId { get; set; }

        protected SnapMessageBase(bool generateRandomRequestId = true)
        {
            if (generateRandomRequestId)
            {
                RequestId = MessageConstants.Random.NextLong();
            }
        }
    }
}
