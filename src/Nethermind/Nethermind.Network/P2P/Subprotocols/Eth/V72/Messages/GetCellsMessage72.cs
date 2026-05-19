// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;

public class GetCellsMessage72(
    Hash256[] hashes,
    byte[] cellMask,
    bool generateRandomRequestId = true)
    : Eth66MessageBase(generateRandomRequestId)
{
    public GetCellsMessage72(long requestId, Hash256[] hashes, byte[] cellMask)
        : this(hashes, cellMask, false) =>
        RequestId = requestId;

    public override int PacketType => Eth72MessageCode.GetCells;
    public override string Protocol => "eth";

    public Hash256[] Hashes { get; } = hashes;
    public byte[] CellMask { get; } = cellMask;

    public override string ToString() => $"{nameof(GetCellsMessage72)}({RequestId}, {Hashes.Length})";
}
