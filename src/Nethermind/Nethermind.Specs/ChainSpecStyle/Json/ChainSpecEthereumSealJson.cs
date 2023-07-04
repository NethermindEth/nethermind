// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Specs.ChainSpecStyle.Json
{
    internal class ChainSpecEthereumSealJson
    {
        public UInt256 Nonce { get; set; }
        public Keccak MixHash { get; set; }
    }
}
