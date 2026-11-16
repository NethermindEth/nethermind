// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc;

/// <summary>Sets the XDC base fee from the gas price tier active at the child block, not from parent gas usage.</summary>
internal class XdcBaseFeeCalculator(XdcChainSpecEngineParameters engineParameters) : IBaseFeeCalculator
{
    /// <summary>The base fee before any gas price tier, <c>params.InitialBaseFee</c> of XDPoSChain.</summary>
    public const long BaseFee = 12500000000;

    public UInt256 Calculate(BlockHeader parent, IEip1559Spec specFor1559) =>
        specFor1559.IsEip1559Enabled ? engineParameters.ResolveBaseFee(parent.Number + 1) : UInt256.Zero;
}
