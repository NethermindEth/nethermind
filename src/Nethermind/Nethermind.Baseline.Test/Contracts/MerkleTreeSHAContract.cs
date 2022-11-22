// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
