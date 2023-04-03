// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

namespace Ethereum.Test.Base
{
    public class HalfBlockchainTestJson : BlockchainTestJson
    {
        public new Keccak PostState { get; set; }
    }

    public class BlockchainTestJson
    {
        public string? Network { get; set; }
        public IReleaseSpec? EthereumNetwork { get; set; }
        public IReleaseSpec? EthereumNetworkAfterTransition { get; set; }
        public int TransitionBlockNumber { get; set; }
        public string? LastBlockHash { get; set; }
        public string? GenesisRlp { get; set; }

        public TestBlockJson[]? Blocks { get; set; }
        public TestBlockHeaderJson? GenesisBlockHeader { get; set; }

        public Dictionary<string, AccountStateJson>? Pre { get; set; }
        public Dictionary<string, AccountStateJson>? PostState { get; set; }

        public Keccak? PostStateHash { get; set; }

        public string? SealEngine { get; set; }
        public string? LoadFailure { get; set; }
    }
}
