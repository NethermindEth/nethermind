// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

namespace Ethereum.Test.Base
{
    public class HalfBlockchainTestJson : BlockchainTestJson
    {
        public new Hash256 PostState { get; set; }
    }

    public class BlockchainTestJson
    {
        public string? Network { get; set; }
        public IReleaseSpec? EthereumNetwork { get; set; }
        public IReleaseSpec? EthereumNetworkAfterTransition { get; set; }
        public ForkActivation? TransitionForkActivation { get; set; }
        public string? LastBlockHash { get; set; }
        public string? GenesisRlp { get; set; }

        public TestBlockJson[]? Blocks { get; set; }
        public TestBlockHeaderJson? GenesisBlockHeader { get; set; }

        public Dictionary<Address, AccountState>? Pre { get; set; }
        public Dictionary<Address, AccountState>? PostState { get; set; }

        public Hash256? PostStateHash { get; set; }

        public string? SealEngine { get; set; }
        public string? LoadFailure { get; set; }
    }
}
