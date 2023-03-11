// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Ethereum.Test.Base.Interfaces;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;

namespace Ethereum.Test.Base
{
    public class BlockchainTest : IEthereumTest
    {
        public string? Category { get; set; }
        public string? Name { get; set; }
        public IReleaseSpec? Network { get; set; }
        public IReleaseSpec? NetworkAfterTransition { get; set; }
        public long TransitionBlockNumber { get; set; }
        public Keccak? LastBlockHash { get; set; }
        public Rlp? GenesisRlp { get; set; }

        public TestBlockJson[]? Blocks { get; set; }
        public TestBlockHeaderJson? GenesisBlockHeader { get; set; }

        public Dictionary<Address, AccountState>? Pre { get; set; }
        public Dictionary<Address, AccountState>? PostState { get; set; }
        public Keccak? PostStateRoot { get; set; }
        public bool SealEngineUsed { get; set; }
        public string? LoadFailure { get; set; }

        public override string? ToString()
        {
            return Name;
        }
    }
}
