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
// 

using System.Net;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.Enr;

namespace Nethermind.Network.Discovery.Messages;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-868
/// </summary>
public class EnrResponseMsg : DiscoveryMsg
{
    private const long MaxTime = long.MaxValue; // non-expiring message
    
    public override MsgType MsgType => MsgType.EnrResponse;

    public NodeRecord NodeRecord { get; }

    public EnrResponseMsg(IPEndPoint farAddress, NodeRecord nodeRecord)
        : base(farAddress, MaxTime)
    {
        NodeRecord = nodeRecord;
    }
    
    public EnrResponseMsg(PublicKey farPublicKey, NodeRecord nodeRecord)
        : base(farPublicKey, MaxTime)
    {
        NodeRecord = nodeRecord;
    }
}
