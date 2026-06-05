// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Xdc;

public class XdcChainSpecLoader
{
    public void ProcessChainSpec(ChainSpec chainSpec)
    {
        if (chainSpec.Genesis is not null)
        {
            Block originalGenesis = chainSpec.Genesis;
            chainSpec.Genesis = originalGenesis.WithReplacedHeader(CreateGenesisHeader(originalGenesis.Header));
        }
    }

    protected virtual BlockHeader CreateGenesisHeader(BlockHeader header) => XdcBlockHeader.FromBlockHeader(header);
}
