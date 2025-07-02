// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Specs.ChainSpecStyle.Json
{
    public class ChainSpecEthereumSealJson
    {
        public UInt256 Nonce { get; set; }
        public Hash256 MixHash { get; set; }
    }
}
