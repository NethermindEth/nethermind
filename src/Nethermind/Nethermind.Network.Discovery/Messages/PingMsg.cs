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

public class PingMsg : DiscoveryMsg
{
    public IPEndPoint SourceAddress { get; }
    public IPEndPoint DestinationAddress { get; }
    public byte[]? Mdc { get; set; }

    public PingMsg(PublicKey farPublicKey, long expirationTime, IPEndPoint source, IPEndPoint destination, byte[] mdc)
        : base(farPublicKey, expirationTime)
    {
        SourceAddress = source ?? throw new ArgumentNullException(nameof(source));
        DestinationAddress = destination ?? throw new ArgumentNullException(nameof(destination));
        Mdc = mdc ?? throw new ArgumentNullException(nameof(mdc));
    }
    
    public PingMsg(IPEndPoint farAddress, long expirationTime, IPEndPoint sourceAddress)
        : base(farAddress, expirationTime)
    {
        SourceAddress = sourceAddress ?? throw new ArgumentNullException(nameof(sourceAddress));
        DestinationAddress = farAddress;
    }
    
    public override string ToString()
    {
        return base.ToString() + $", SourceAddress: {SourceAddress}, DestinationAddress: {DestinationAddress}, Version: {Version}, Mdc: {Mdc?.ToHexString()}";
    }

    public override MsgType MsgType => MsgType.Ping;
}
