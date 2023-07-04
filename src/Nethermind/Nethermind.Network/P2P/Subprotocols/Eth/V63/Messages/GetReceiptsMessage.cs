// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages
{
    public class GetReceiptsMessage : HashesMessage
    {
        public override int PacketType { get; } = Eth63MessageCode.GetReceipts;
        public override string Protocol { get; } = "eth";

        public GetReceiptsMessage(IReadOnlyList<Keccak> blockHashes)
            : base(blockHashes)
        {
        }
    }
}
