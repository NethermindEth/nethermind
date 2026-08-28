// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Xdc.Spec;

public class XdcSubnetChainSpecEngineParameters : XdcChainSpecEngineParameters
{
    public override string SealEngineType => XdcConstants.XDPoSSubnet;
}
