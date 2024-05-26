// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;

public class ReceiptMessageDecoder69 : ReceiptMessageDecoder
{
    public ReceiptMessageDecoder69() : base(includeBloom: false) { }
}
