// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Core;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Xdc;

public class XdcChainSpecLoader(IChainSpecLoader baseLoader) : IChainSpecLoader
{
    public ChainSpec Load(Stream streamData)
    {
        ChainSpec chainSpec = baseLoader.Load(streamData);
        
        if (chainSpec.Genesis is not null)
        {
            Block originalGenesis = chainSpec.Genesis;
            chainSpec.Genesis = originalGenesis.WithReplacedHeader(
                XdcBlockHeader.FromBlockHeader(originalGenesis.Header)
            );
        }
        
        return chainSpec;
    }
}
