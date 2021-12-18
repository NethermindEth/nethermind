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
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Messages;

public class NeighborsMsg : DiscoveryMsg
{
    public Node[] Nodes { get; init; }

    public NeighborsMsg(IPEndPoint farAddress, long expirationTime, Node[] nodes) : base(farAddress, expirationTime)
    {
        Nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
    }
    
    public NeighborsMsg(PublicKey farPublicKey, long expirationTime, Node[] nodes) : base(farPublicKey, expirationTime)
    {
        Nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
    }
    
    public override string ToString()
    {
        return base.ToString() + $", Nodes: {(Nodes.Any() ? string.Join(",", Nodes.Select(x => x.ToString())) : "empty")}";
    }
        
    public override MsgType MsgType => MsgType.Neighbors;
}
