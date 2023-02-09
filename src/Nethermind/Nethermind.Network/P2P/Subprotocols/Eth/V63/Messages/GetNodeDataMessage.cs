// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages
{
    public class GetNodeDataMessage : HashesMessage
    {
        public override int PacketType { get; } = Eth63MessageCode.GetNodeData;
        public override string Protocol { get; } = "eth";

        public GetNodeDataMessage(IReadOnlyList<Keccak> keys)
            : base(keys)
        {
        }
    }
}
