// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;

public class CellsMessage72(
    Hash256[] hashes,
    byte[][][] cells,
    byte[] cellMask,
    bool generateRandomRequestId = true)
    : Eth66MessageBase(generateRandomRequestId)
{
    public CellsMessage72(long requestId, Hash256[] hashes, byte[][][] cells, byte[] cellMask)
        : this(hashes, cells, cellMask, false) =>
        RequestId = requestId;

    public override int PacketType => Eth72MessageCode.Cells;
    public override string Protocol => "eth";

    public Hash256[] Hashes { get; } = hashes;

    /// <summary>
    /// Gets cell groups by transaction, flattened in blob-major order for internal consumers.
    /// </summary>
    /// <remarks>The serializer transposes each group to the cell-index-major eth/72 wire order.</remarks>
    public byte[][][] Cells { get; } = cells;
    public byte[] CellMask { get; } = cellMask;

    public override string ToString() => $"{nameof(CellsMessage72)}({RequestId}, {Hashes.Length})";
}
