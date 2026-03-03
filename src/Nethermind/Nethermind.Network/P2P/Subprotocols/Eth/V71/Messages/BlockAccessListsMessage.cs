// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V71.Messages;

/// <summary>
/// Response to GetBlockAccessLists. Each element corresponds to a block hash from the request, in order.
/// Elements are RLP-encoded BALs (raw bytes). Empty bytes (0xc0) for blocks where the BAL is unavailable.
/// </summary>
public class BlockAccessListsMessage(IOwnedReadOnlyList<byte[]> accessLists) : P2PMessage
{
    public static readonly byte[] EmptyBal = [0xc0];

    public IOwnedReadOnlyList<byte[]> AccessLists { get; } = accessLists ?? throw new ArgumentNullException(nameof(accessLists));

    public override int PacketType => Eth71MessageCode.BlockAccessLists;
    public override string Protocol => "eth";

    public override string ToString() => $"BlockAccessLists({AccessLists.Count})";

    public override void Dispose()
    {
        base.Dispose();
        AccessLists.Dispose();
    }
}
