// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages
{
    public class ReceiptsMessage69 : Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.ReceiptsMessage
    {
        public ReceiptsMessage69(long requestId, IOwnedReadOnlyList<TxReceipt[]> txReceipts)
            : base(requestId, txReceipts) { }

        public ReceiptsMessage69(long requestId, V63.Messages.ReceiptsMessage ethMessage)
            : base(requestId, ethMessage) { }
    }
}
