// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Xdc.Spec;

// Same parameter payload as XDPoS, but mapped to subnet seal engine type.
public class XdcSubnetChainSpecEngineParameters : XdcChainSpecEngineParameters, IChainSpecEngineParameters
{
    string? IChainSpecEngineParameters.EngineName => XdcConstants.XDPoSSubnet;
    string? IChainSpecEngineParameters.SealEngineType => XdcConstants.XDPoSSubnet;
}
