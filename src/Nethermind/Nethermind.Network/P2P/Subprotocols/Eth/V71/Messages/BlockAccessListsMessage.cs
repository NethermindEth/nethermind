// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V71.Messages;

/// <summary>
/// Response to GetBlockAccessLists. Each entry is an already RLP-encoded BAL, or null when the BAL is unavailable.
/// </summary>
public class BlockAccessListsMessage(IOwnedReadOnlyList<byte[]?> blockAccessLists, bool generateRandomRequestId = true)
    : Eth66MessageBase(generateRandomRequestId)
{
    private IOwnedReadOnlyList<byte[]?>? _blockAccessLists = blockAccessLists;

    public override int PacketType => Eth71MessageCode.BlockAccessLists;
    public override string Protocol => "eth";

    public IOwnedReadOnlyList<byte[]?> BlockAccessLists =>
        _blockAccessLists ?? throw new ObjectDisposedException(nameof(BlockAccessListsMessage));

    public BlockAccessListsMessage(long requestId, IOwnedReadOnlyList<byte[]?> blockAccessLists)
        : this(blockAccessLists, false) => RequestId = requestId;

    /// <summary>
    /// Transfers the owned BAL response list to the caller so disposing this message will not dispose it.
    /// </summary>
    public IOwnedReadOnlyList<byte[]?> DisownBlockAccessLists()
    {
        IOwnedReadOnlyList<byte[]?> blockAccessLists = BlockAccessLists;
        _blockAccessLists = null;
        return blockAccessLists;
    }

    public override string ToString() => $"BlockAccessLists({RequestId}, {BlockAccessLists.Count})";

    public override void Dispose()
    {
        base.Dispose();
        _blockAccessLists?.Dispose();
        _blockAccessLists = null;
    }
}
