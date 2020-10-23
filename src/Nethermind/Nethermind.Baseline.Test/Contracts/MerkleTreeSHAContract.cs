//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System.Linq;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Baseline.Test.Contracts
{
    internal class MerkleTreeSHAContract : Contract
    {
        public MerkleTreeSHAContract(
            IAbiEncoder abiEncoder,
            Address contractAddress)
            : base(abiEncoder, contractAddress)
        {
        }

        public Transaction InsertLeaf(Address sender, Keccak hash) => GenerateTransaction<GeneratedTransaction>(nameof(InsertLeaf), sender, hash.Bytes);

        public Transaction InsertLeaves(Address sender, Keccak[] hashes)
        {
            byte[][] bytes = hashes.Select(x => x.Bytes).ToArray();
            return GenerateTransaction<GeneratedTransaction>(nameof(InsertLeaves), sender, (object)bytes);
        }
    }
}
