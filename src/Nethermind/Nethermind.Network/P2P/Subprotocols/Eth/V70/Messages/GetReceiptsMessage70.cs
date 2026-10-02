// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V70.Messages;

public class GetReceiptsMessage70(
    IOwnedReadOnlyList<Hash256> hashes,
    long firstBlockReceiptIndex = 0,
    bool generateRandomRequestId = true)
    : Eth66MessageBase(generateRandomRequestId)
{
    public IOwnedReadOnlyList<Hash256> Hashes { get; } = hashes ?? throw new ArgumentNullException(nameof(hashes));
    public long FirstBlockReceiptIndex { get; set; } = firstBlockReceiptIndex;

    /// <summary>
    /// Per requested block, the most receipts a response may hold for it, negative when unknown; <c>null</c> when no limit is known.
    /// For the first block it excludes the receipts before <see cref="FirstBlockReceiptIndex"/>.
    /// </summary>
    /// <remarks>Local bookkeeping for checking the response; not sent to the peer.</remarks>
    public int[]? MaxReceiptsPerBlock { get; init; }

    /// <summary>
    /// The number of blocks requested, kept because the message, with its pooled hashes, is disposed once it is sent.
    /// </summary>
    public int RequestedBlocks { get; } = hashes?.Count ?? 0;

    public override int PacketType => Eth70MessageCode.GetReceipts;
    public override string Protocol => "eth";

    public GetReceiptsMessage70(long requestId, long firstBlockReceiptIndex, IOwnedReadOnlyList<Hash256> hashes)
        : this(hashes, firstBlockReceiptIndex, false) => RequestId = requestId;

    public override string ToString() => $"GetReceipts70({RequestId}, start={FirstBlockReceiptIndex}, {Hashes.Count})";

    public override void Dispose()
    {
        base.Dispose();
        Hashes.Dispose();
    }
}
