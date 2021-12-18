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
/// https://github.com/ethereum/devp2p/blob/master/enr-entries/eth.md
/// </summary>
public class EthEntry : EnrContentEntry<ForkId>
{
    public EthEntry(byte[] forkHash, long nextBlock) : base(new ForkId(forkHash, nextBlock)) { }

    public override string Key => EnrContentKey.Eth;

    protected override int GetRlpLengthOfValue()
    {
        return Rlp.LengthOfSequence(
                Rlp.LengthOfSequence(
                    5 + Rlp.LengthOf(Value.NextBlock)));
    }

    protected override void EncodeValue(RlpStream rlpStream)
    {
        // I am just guessing this one
        int contentLength = 5 + Rlp.LengthOf(Value.NextBlock);
        rlpStream.StartSequence(contentLength + 1);
        rlpStream.StartSequence(contentLength);
        rlpStream.Encode(Value.ForkHash);
        rlpStream.Encode(Value.NextBlock);
    }
}
