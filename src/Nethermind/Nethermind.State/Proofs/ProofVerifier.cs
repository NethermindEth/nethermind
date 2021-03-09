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

using System.IO;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Proofs
{
    public static class ProofVerifier
    {
        public static byte[]? Verify(byte[][] proof, Keccak root)
        {
            if (proof.Length == 0)
            {
                return null;
            }

            TrieNode trieNode = new(NodeType.Unknown, proof.Last());
            trieNode.ResolveNode(null);
            for (int i = proof.Length; i > 0; i--)
            {
                Keccak proofHash = Keccak.Compute(proof[i - 1]);
                if (i > 1)
                {
                    if (!new Rlp(proof[i - 2]).ToString(false).Contains(proofHash.ToString(false)))
                    {
                        throw new InvalidDataException();
                    }
                }
                else
                {
                    if (proofHash != root)
                    {
                        throw new InvalidDataException();
                    }
                }
            }

            return trieNode.Value;
        }
    }
}
