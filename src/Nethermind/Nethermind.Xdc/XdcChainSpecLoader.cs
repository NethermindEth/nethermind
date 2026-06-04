// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Xdc.Spec;
using System.Linq;

namespace Nethermind.Xdc;

public static class XdcChainSpecLoader
{
    public static void ProcessChainSpec(ChainSpec chainSpec)
    {
        if (chainSpec.Genesis is not null)
        {
            Block originalGenesis = chainSpec.Genesis;
            bool isSubnet =
                chainSpec.EngineChainSpecParametersProvider.AllChainSpecParameters.OfType<XdcSubnetChainSpecEngineParameters>().Any()
                || chainSpec.SealEngineType == XdcConstants.XDPoSSubnet;
            BlockHeader header;
            if (isSubnet)
            {
                XdcSubnetBlockHeader subnetHeader = XdcSubnetBlockHeader.FromBlockHeader(originalGenesis.Header);
                // XDC-Subnet genesis encodes these fields as empty values, not null.
                subnetHeader.Validator = [];
                subnetHeader.Validators = [];
                subnetHeader.NextValidators = [];
                subnetHeader.Penalties = [];
                header = subnetHeader;
            }
            else
            {
                header = XdcBlockHeader.FromBlockHeader(originalGenesis.Header);
            }

            chainSpec.Genesis = originalGenesis.WithReplacedHeader(header);
        }
    }
}
