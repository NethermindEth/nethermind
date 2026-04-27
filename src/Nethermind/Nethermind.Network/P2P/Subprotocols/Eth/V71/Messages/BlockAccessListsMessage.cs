// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V71.Messages;

/// <summary>
/// Response to GetBlockAccessLists. Each element corresponds to a block hash from the request, in order.
/// Elements are RLP-encoded BALs (raw bytes). The RLP empty string (0x80) is returned where the BAL is unavailable.
/// </summary>
public class BlockAccessListsMessage(IOwnedReadOnlyList<byte[]> blockAccessLists, bool generateRandomRequestId = true)
    : Eth66MessageBase(generateRandomRequestId)
{
    public static readonly byte[] EmptyBal = [Rlp.EmptyByteArrayByte];

    public IOwnedReadOnlyList<byte[]> BlockAccessLists { get; } = blockAccessLists ?? throw new ArgumentNullException(nameof(blockAccessLists));

    public override int PacketType => Eth71MessageCode.BlockAccessLists;
    public override string Protocol => "eth";

    public BlockAccessListsMessage(long requestId, IOwnedReadOnlyList<byte[]> blockAccessLists)
        : this(blockAccessLists, false) => RequestId = requestId;

    public override string ToString() => $"BlockAccessLists({RequestId}, {BlockAccessLists.Count})";

    public override void Dispose()
    {
        base.Dispose();
        BlockAccessLists.Dispose();
    }
}
