// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Specs.ChainSpecStyle.Json
{
    public class ChainSpecEthereumSealJson
    {
        public ulong Nonce { get; set; }
        public Hash256 MixHash { get; set; }
    }
}
