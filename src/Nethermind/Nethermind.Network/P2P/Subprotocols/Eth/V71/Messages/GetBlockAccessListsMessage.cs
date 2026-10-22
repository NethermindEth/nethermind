// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V71.Messages;

public class GetBlockAccessListsMessage(IOwnedReadOnlyList<Hash256> blockHashes, bool generateRandomRequestId = true)
    : Eth66MessageBase(generateRandomRequestId)
{
    public IOwnedReadOnlyList<Hash256> Hashes { get; } = blockHashes ?? throw new ArgumentNullException(nameof(blockHashes));

    public override int PacketType => Eth71MessageCode.GetBlockAccessLists;
    public override string Protocol => "eth";

    public GetBlockAccessListsMessage(long requestId, IOwnedReadOnlyList<Hash256> blockHashes)
        : this(blockHashes, false) => RequestId = requestId;

    public override void Dispose()
    {
        base.Dispose();
        Hashes.Dispose();
    }

    public override string ToString() => $"{nameof(GetBlockAccessListsMessage)}({RequestId}, {Hashes.Count})";
}
