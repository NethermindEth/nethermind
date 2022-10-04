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

using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Enr;

/// <summary>
/// An entry storing TCP IPv4 port number.
/// </summary>
public class TcpEntry : EnrContentEntry<int>
{
    public TcpEntry(int portNumber) : base(portNumber) { }

    public override string Key => EnrContentKey.Tcp;

    protected override int GetRlpLengthOfValue()
    {
        return Rlp.LengthOf(Value);
    }

    protected override void EncodeValue(RlpStream rlpStream)
    {
        rlpStream.Encode(Value);
    }
}
