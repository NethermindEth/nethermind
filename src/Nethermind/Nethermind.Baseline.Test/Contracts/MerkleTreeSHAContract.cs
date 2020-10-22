using System;
using System.Collections.Generic;
using System.Text;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;

namespace Nethermind.Baseline.Test.Contracts
{
    internal class MerkleTreeSHAContract : Contract
    {
        public MerkleTreeSHAContract(
            IAbiEncoder abiEncoder,
            Address contractAddress,
            IReadOnlyTransactionProcessorSource readOnlyTransactionProcessorSource)
            : base(abiEncoder, contractAddress)
        {
        }

        public Transaction InsertLeaf(Address sender, Keccak bytes) => GenerateTransaction<GeneratedTransaction>(nameof(InsertLeaf), sender, bytes.Bytes);
    }
}
