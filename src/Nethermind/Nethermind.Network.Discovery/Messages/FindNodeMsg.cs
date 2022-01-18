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

using System.Net;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Network.Discovery.Messages;

public class FindNodeMsg : DiscoveryMsg
{
    public byte[] SearchedNodeId { get; set; }

    public override string ToString()
    {
        return base.ToString() + $", SearchedNodeId: {SearchedNodeId.ToHexString()}";
    }

    public override MsgType MsgType => MsgType.FindNode;

    public FindNodeMsg(IPEndPoint farAddress, long expirationDate, byte[] searchedNodeId)
        : base(farAddress, expirationDate)
    {
        SearchedNodeId = searchedNodeId;
    }
    
    public FindNodeMsg(PublicKey farPublicKey, long expirationDate, byte[] searchedNodeId)
        : base(farPublicKey, expirationDate)
    {
        SearchedNodeId = searchedNodeId;
    }
}
