// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V70.Messages;

public class GetReceiptsMessage70 : V63.Messages.GetReceiptsMessage, IEth66Message
{
    public long RequestId { get; set; } = MessageConstants.Random.NextLong();
    public long FirstBlockReceiptIndex { get; set; }

    public GetReceiptsMessage70(IOwnedReadOnlyList<Hash256> blockHashes)
        : this(MessageConstants.Random.NextLong(), 0, blockHashes)
    {
    }

    public GetReceiptsMessage70(long requestId, long firstBlockReceiptIndex, IOwnedReadOnlyList<Hash256> blockHashes)
        : base(blockHashes)
    {
        RequestId = requestId;
        FirstBlockReceiptIndex = firstBlockReceiptIndex;
    }

    public GetReceiptsMessage70(long requestId, long firstBlockReceiptIndex, V63.Messages.GetReceiptsMessage ethMessage)
        : this(requestId, firstBlockReceiptIndex, ethMessage.Hashes)
    {
    }

    public override string ToString() => $"GetReceipts70({RequestId}, start={FirstBlockReceiptIndex}, {base.ToString()})";
}
