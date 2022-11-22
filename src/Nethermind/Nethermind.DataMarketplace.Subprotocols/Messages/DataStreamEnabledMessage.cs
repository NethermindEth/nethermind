// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.DataMarketplace.Subprotocols.Messages
{
    public class DataStreamEnabledMessage : P2PMessage
    {
        public override int PacketType { get; } = NdmMessageCode.DataStreamEnabled;
        public override string Protocol => "ndm";
        public Keccak DepositId { get; }
        public string Client { get; }
        public string?[] Args { get; }

        public DataStreamEnabledMessage(Keccak depositId, string client, string?[] args)
        {
            DepositId = depositId;
            Client = client;
            Args = args;
        }
    }
}
