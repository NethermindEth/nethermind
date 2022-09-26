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
/// An entry storing the signature scheme version - hardcoded to 'v4'.
/// </summary>
public class IdEntry : EnrContentEntry<string>
{
    private IdEntry() : base("v4") { }

    public static IdEntry Instance { get; } = new();

    public override string Key => EnrContentKey.Id;

    protected override int GetRlpLengthOfValue()
    {
        return Rlp.LengthOf(Value);
    }

    protected override void EncodeValue(RlpStream rlpStream)
    {
        rlpStream.Encode("v4");
    }
}
