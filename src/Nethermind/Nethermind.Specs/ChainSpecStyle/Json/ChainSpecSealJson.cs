// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.ChainSpecStyle.Json
{
    internal class ChainSpecSealJson
    {
        public ChainSpecEthereumSealJson Ethereum { get; set; }
        public ChainSpecAuRaSealJson AuthorityRound { get; set; }
    }
}
