// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Ethereum.Test.Base
{
    public class BlockchainTest : EthereumTest
    {
        public string? ForkName { get; set; }
        public IReleaseSpec? Network { get; set; }
        public IReleaseSpec? NetworkAfterTransition { get; set; }
        public ForkActivation? TransitionForkActivation { get; set; }

        /// <summary>
        /// The terminal total difficulty of a PoW to PoS transition network named <c>{from}To{to}AtDiff{hex}</c>,
        /// where the merge happens at the first block whose parent reaches this total difficulty rather than at a
        /// block number or timestamp. Null for every other network.
        /// </summary>
        public UInt256? TransitionTerminalTotalDifficulty { get; set; }
        public Hash256? LastBlockHash { get; set; }
        public Rlp? GenesisRlp { get; set; }

        public TestBlockJson[]? Blocks { get; set; }
        public TestBlockHeaderJson? GenesisBlockHeader { get; set; }
        public TestEngineNewPayloadsJson[]? EngineNewPayloads { get; set; }

        public Dictionary<Address, AccountState>? Pre { get; set; }
        public Dictionary<Address, AccountState>? PostState { get; set; }
        public Hash256? PostStateRoot { get; set; }

        public override string? ToString() => Name;
    }
}
