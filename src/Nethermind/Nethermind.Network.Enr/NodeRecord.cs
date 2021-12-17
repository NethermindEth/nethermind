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

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Enr;

public class NodeRecord
{
    private SortedDictionary<string, EnrContentEntry> Entries { get; set; } = new();

    public int Sequence { get; set; } = 0;

    public void SetEntry(EnrContentEntry entry)
    {
        if (Entries.ContainsKey(entry.Key))
        {
            Sequence++;
        }

        Entries[entry.Key] = entry;
    }

    public int GetRlpLengthWithSignature()
    {
        int contentLength = Rlp.LengthOf(Sequence); // this is a different meaning of a sequence than the RLP sequence
        contentLength += 64 + 1; // signature RLP length
        foreach ((_, EnrContentEntry enrContentEntry) in Entries)
        {
            contentLength += enrContentEntry.GetRlpLength();
        }

        return Rlp.LengthOfSequence(contentLength);
    }

    public void Encode(RlpStream rlpStream)
    {
        rlpStream.Encode(Sequence);
        foreach ((_, EnrContentEntry contentEntry) in Entries.OrderBy(e => e.Key))
        {
            contentEntry.Encode(rlpStream);
        }
    }
    
    public void Encode(RlpStream rlpStream, Signature signature)
    {
        rlpStream.Encode(signature.Bytes);
        rlpStream.Encode(Sequence);
        foreach ((_, EnrContentEntry contentEntry) in Entries.OrderBy(e => e.Key))
        {
            contentEntry.Encode(rlpStream);
        }
    }
}
