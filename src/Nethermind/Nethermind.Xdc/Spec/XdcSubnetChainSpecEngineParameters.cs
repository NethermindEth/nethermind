// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Xdc.Spec;

public class XdcSubnetChainSpecEngineParameters : XdcChainSpecEngineParameters
{
    public override string SealEngineType => XdcConstants.XDPoSSubnet;

    /// <inheritdoc/>
    /// <remarks>
    /// The subnet fork's <c>common.GetMinGasPrice</c> has no 50x transition - it always returns the raised floor - and
    /// honours a gasless node, which its <c>--gasprice 0</c> enables. A subnet chainspec therefore states
    /// <see cref="XdcChainSpecEngineParameters.MinGasPrice"/> as zero to run gasless, and
    /// <see cref="Gas50xBlock"/> is not consulted at all.
    /// </remarks>
    internal override UInt256 ResolveMinGasPrice(ulong blockNumber) =>
        MinGasPrice == UInt256.Zero
            ? UInt256.Zero
            : ConfiguredMinGasPrice * XdcConstants.Gas50xMultiplier;
}
