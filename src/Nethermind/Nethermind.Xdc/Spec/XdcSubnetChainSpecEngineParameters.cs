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
    /// <para>
    /// A subnet chainspec must not activate EIP-1559: the subnet fork's header type carries no base fee field at all,
    /// which <see cref="RLP.XdcSubnetHeaderDecoder"/> mirrors, so the value could not round-trip. Were it activated
    /// anyway, <see cref="XdcBaseFeeCalculator"/>'s constant would apply on top of a zero floor and strand zero-price
    /// transactions that no peer can mine.
    /// </para>
    /// </remarks>
    internal override UInt256 ResolveMinGasPrice(ulong blockNumber) =>
        // Unset is not zero: only an explicit zero means gasless.
        MinGasPrice is { IsZero: true }
            ? UInt256.Zero
            : ConfiguredMinGasPrice * XdcConstants.Gas50xMultiplier;
}
