// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V71.Messages;

public class GetBlockAccessListsMessage(IOwnedReadOnlyList<Hash256> blockHashes) : HashesMessage(blockHashes)
{
    public override int PacketType => Eth71MessageCode.GetBlockAccessLists;
    public override string Protocol => "eth";
}
