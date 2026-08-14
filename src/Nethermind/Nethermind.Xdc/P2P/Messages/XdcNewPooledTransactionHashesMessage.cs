// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages;

namespace Nethermind.Xdc.P2P.Messages;

/// <summary>
/// <see cref="NewPooledTransactionHashesMessage"/> on the code XDC relocated it to.
/// </summary>
/// <remarks>
/// Sent as its base type, so the upstream serializer encodes it - serializer lookup binds the static type at
/// the send site. Only the packet code differs, and that comes from this class.
/// </remarks>
public class XdcNewPooledTransactionHashesMessage(IOwnedReadOnlyList<Hash256> hashes) : NewPooledTransactionHashesMessage(hashes)
{
    public override int PacketType => XdcMessageCode.NewPooledTransactionHashes;

    public override string ToString() => $"{nameof(XdcNewPooledTransactionHashesMessage)}({Hashes?.Count})";
}
