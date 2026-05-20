// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Xdc;

public static class XdcChainSpecLoader
{
    public static void ProcessChainSpec(ChainSpec chainSpec)
    {
        if (chainSpec.Genesis is not null)
        {
            Block originalGenesis = chainSpec.Genesis;
            chainSpec.Genesis = originalGenesis.WithReplacedHeader(
                XdcBlockHeader.FromBlockHeader(originalGenesis.Header)
            );
        }
    }
}
