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

using System.Data;
using System.Net;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Enr;

public class IpEntry : EnrContentEntry<IPAddress>
{
    public IpEntry(IPAddress ipAddress) : base(ipAddress) { }

    public override string Key => EnrContentKey.Ip;
    
    protected override int GetRlpLengthOfValue()
    {
        return 5;
    }

    protected override void EncodeValue(RlpStream rlpStream)
    {
        Span<byte> bytes = stackalloc byte[4];
        Value.MapToIPv4().TryWriteBytes(bytes, out int bytesWritten);

        if (bytesWritten != 4)
        {
            throw new DataException($"Invalid ENR record - bytes written {bytesWritten} when encoding IP");
        }
        
        rlpStream.Encode(bytes);
    }
}
