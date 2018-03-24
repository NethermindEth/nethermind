using System.Collections.Generic;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Specs;

namespace Ethereum.Test.Base
{
    public class BlockchainTest
    {
        public string Name { get; set; }
        public IReleaseSpec Network { get; set; }
        public IReleaseSpec NetworkAfterTransition { get; set; }
        public BigInteger TransitionBlockNumber { get; set; }
        public Keccak LastBlockHash { get; set; }
        public Rlp GenesisRlp { get; set; }

        public TestBlockJson[] Blocks { get; set; }
        public TestBlockHeaderJson GenesisBlockHeader { get; set; }

        public Dictionary<Address, AccountState> Pre { get; set; }
        public Dictionary<Address, AccountState> PostState { get; set; }

        public override string ToString()
        {
            return Name;
        }
    }
}