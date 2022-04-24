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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Proofs
{
    public static class ProofVerifier
    {
        /// <summary>
        /// Verifies one proof - address path from the bottom to the root.
        /// </summary>
        /// <returns>The Value of the bottom most proof node. For example an Account.</returns>
        public static byte[]? VerifyOneProof(byte[][] proof, Keccak root)
        {
            if (proof.Length == 0)
            {
                return null;
            }

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

            TrieNode trieNode = new(NodeType.Unknown, proof.Last());
            trieNode.ResolveNode(null);

            return trieNode.Value;
        }

        /// <summary>
        /// Verifies multiple proofs - address paths from the bottom to the root.
        /// Proofs are aligned one after another. Each proof should start with a root node to be proved correct. 
        /// </summary>
        /// <returns>True - if all proofs are proved to be correct
        /// List of proved values</returns>
        public static (bool provedToBeCorrect, IList<byte[]?> provedValues) VerifyMultipleProofs(byte[][] proofs, Keccak root)
        {
            if (proofs.Length == 0)
            {
                return (false, null);
            }

            List<byte[]?> provedValues = new();
            int leafIndex = proofs.Length - 1;

            var rlps = proofs.Select(p => $"{Keccak.Compute(p).ToString(false)}:{new Rlp(p).ToString(false)}").ToArray();

            var res = string.Join($"{Environment.NewLine}{Environment.NewLine}", rlps);

            for (int i = proofs.Length - 1; i >= 0; i--)
            {
                Keccak proofHash = Keccak.Compute(proofs[i]);
                if (proofHash != root)
                {
                    if (i > 0)
                    {
                        if (!new Rlp(proofs[i - 1]).ToString(false).Contains(proofHash.ToString(false)))
                        {
                            return (false, provedValues);
                        }
                    }
                    else
                    {
                        return (false, provedValues);
                    }
                }
                else
                {
                    TrieNode trieNode = new(NodeType.Unknown, proofs[leafIndex]);
                    trieNode.ResolveNode(null);
                    provedValues.Add(trieNode.Value);

                    leafIndex = i - 1;
                }
            }

            return (true, provedValues);
        }
    }
}
