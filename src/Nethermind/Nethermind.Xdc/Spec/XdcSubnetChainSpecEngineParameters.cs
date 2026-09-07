// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Xdc.Spec;

public class XdcSubnetChainSpecEngineParameters : XdcChainSpecEngineParameters
{
    // The subnet reference client has no 50x transition - its GetMinGasPrice always returns MinGasPrice50x - so the
    // raised gas price floor applies from genesis. A chainspec that states Gas50xBlock still overrides this.
    public XdcSubnetChainSpecEngineParameters() => Gas50xBlock = 0;

    public override string SealEngineType => XdcConstants.XDPoSSubnet;
}
