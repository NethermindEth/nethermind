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

namespace Nethermind.Network.Discovery.Messages;

public abstract class DiscoveryMsg : MessageBase
{
    /// <summary>
    /// For incoming messages far address is set after deserialization
    /// </summary>
    public IPEndPoint? FarAddress { get; set; }
        
    public PublicKey? FarPublicKey { get; init; }

    public int Version { get; set; } = 4;
    
    protected DiscoveryMsg(IPEndPoint farAddress, long expirationTime)
    {
        FarAddress = farAddress ?? throw new ArgumentNullException(nameof(farAddress));
        ExpirationTime = expirationTime;
    }
    
    protected DiscoveryMsg(PublicKey? farPublicKey, long expirationTime)
    {
        FarPublicKey = farPublicKey; // if it is null then it suggests that the signature is not correct
        ExpirationTime = expirationTime;
    }
    
    /// <summary>
    /// Message expiry time as Unix epoch seconds
    /// </summary>
    public long ExpirationTime { get; init; }     

    public override string ToString()
    {
        return $"Type: {MsgType}, FarAddress: {FarAddress?.ToString() ?? "empty"}, FarPublicKey: {FarPublicKey?.ToString() ?? "empty"}, ExpirationTime: {ExpirationTime}";
    }

    public abstract MsgType MsgType { get; }
}
