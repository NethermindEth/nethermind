// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Les.Messages
{
    public class GetContractCodesMessage : P2PMessage
    {
        public override int PacketType { get; } = LesMessageCode.GetContractCodes;
        public override string Protocol { get; } = Contract.P2P.Protocol.Les;
        public long RequestId;
        public CodeRequest[] Requests;

        public Keccak[] RequestAddresses =>
            Requests.Select(request => request.AccountKey).ToArray();

        public GetContractCodesMessage()
        {
        }

        public GetContractCodesMessage(CodeRequest[] requests, long requestId)
        {
            Requests = requests;
            RequestId = requestId;
        }
    }
}
