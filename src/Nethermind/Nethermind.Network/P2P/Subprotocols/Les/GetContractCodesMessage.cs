//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Linq;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.P2P.Subprotocols.Les
{
    public class GetContractCodesMessage: P2PMessage
    {
        public override int PacketType { get; } = LesMessageCode.GetContractCodes;
        public override string Protocol { get; } = P2P.Protocol.Les;
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
