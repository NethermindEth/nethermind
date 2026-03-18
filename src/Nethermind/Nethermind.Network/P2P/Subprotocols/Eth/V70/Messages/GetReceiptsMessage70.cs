// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V70.Messages;

public class GetReceiptsMessage70 : Eth66MessageBase
{
    public IOwnedReadOnlyList<Hash256> Hashes { get; }
    public long FirstBlockReceiptIndex { get; set; }

    public override int PacketType => Eth70MessageCode.GetReceipts;
    public override string Protocol => "eth";

    public GetReceiptsMessage70(IOwnedReadOnlyList<Hash256> hashes, long firstBlockReceiptIndex = 0, bool generateRandomRequestId = true)
        : base(generateRandomRequestId)
    {
        Hashes = hashes ?? throw new ArgumentNullException(nameof(hashes));
        FirstBlockReceiptIndex = firstBlockReceiptIndex;
    }

    public GetReceiptsMessage70(long requestId, long firstBlockReceiptIndex, IOwnedReadOnlyList<Hash256> hashes)
        : this(hashes, firstBlockReceiptIndex, false)
    {
        RequestId = requestId;
    }

    public override string ToString() => $"GetReceipts70({RequestId}, start={FirstBlockReceiptIndex}, {Hashes.Count})";

    public override void Dispose()
    {
        base.Dispose();
        Hashes.Dispose();
    }
}
