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

using System;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Trie
{
    public interface ITree
    {
        TrieType TrieType { get; }
        Keccak RootHash { get; set; }
        // TODO: this method is used for supporting pruning, but it is not clear if it is needed or supported by
        // TODO: verkle tries. look into it and decide accordingly if this needs to be implemented here?
        // void Commit(long blockNumber);
        void UpdateRootHash();
        byte[]? Get(Span<byte> rawKey, Keccak? rootHash = null);

        void Set(Span<byte> rawKey, byte[] value);

        // void Set(Span<byte> rawKey, Rlp? value);
        // void Accept(ITreeVisitor visitor, Keccak rootHash, VisitingOptions visitingOptions = VisitingOptions.ExpectAccounts);
    }
}


// TODO: why using span here?
