// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.TxPool;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.TxPool;

namespace Nethermind.Xdc.RPC;

/// <summary>
/// Raises the gas price suggestions of <paramref name="inner"/> to the minimum the transaction pool enforces, so the
/// node never suggests a price it would then reject.
/// </summary>
/// <remarks>
/// <see cref="MinGasPriceFilter"/> rejects transactions paying less than <see cref="IXdcReleaseSpec.MinimumGasPrice"/>,
/// but the default oracle floors its estimate on <c>Blocks.MinGasPrice</c>, which the XDC configs deliberately set to
/// zero. Before EIP-1559 there is no base fee to lift the estimate, so <c>eth_gasPrice</c> answers with that zero floor
/// and a caller that follows it gets <c>zero gas price</c> or <c>under min gas price</c> back from the same node.
/// <para>
/// The floor cannot instead be handed to the default oracle as its <c>Blocks.MinGasPrice</c>: that value is added to
/// the base fee and scaled by 110%, so once EIP-1559 is live it would suggest roughly twice the minimum rather than the
/// intended margin over it. Clamping the finished estimate keeps the margin intact, and after EIP-1559 the estimate
/// already clears the floor so the clamp does not bind.
/// </para>
/// <para>
/// The clamp is applied after the inner oracle's <see cref="EthGasPriceConstants.MaxGasPrice"/> ceiling and may exceed
/// it on a chainspec whose configured floor is higher. That is deliberate: capping to the ceiling would hand back a
/// price <see cref="MinGasPriceFilter"/> rejects, which is what this decorator exists to prevent.
/// </para>
/// </remarks>
internal sealed class XdcGasPriceOracle(
    IGasPriceOracle inner,
    ISpecProvider specProvider,
    IChainHeadInfoProvider chainHeadInfoProvider) : IGasPriceOracle
{
    public async ValueTask<UInt256> GetGasPriceEstimate() =>
        UInt256.Max(await inner.GetGasPriceEstimate(), NextBlockSpec().MinimumGasPrice);

    public UInt256 GetMaxPriorityGasFeeEstimate()
    {
        UInt256 estimate = inner.GetMaxPriorityGasFeeEstimate();
        IXdcReleaseSpec spec = NextBlockSpec();

        // With EIP-1559 the base fee already covers the floor and the tip sits on top of it, so clamping here would
        // demand the minimum twice over. Without it the tip is the whole price and has to clear the floor itself.
        return spec.IsEip1559Enabled ? estimate : UInt256.Max(estimate, spec.MinimumGasPrice);
    }

    /// <remarks>An estimate is for a transaction that goes into the next block, which is the block
    /// <see cref="MinGasPriceFilter"/> admits against.</remarks>
    private IXdcReleaseSpec NextBlockSpec() => specProvider.GetXdcSpec(chainHeadInfoProvider.HeadNumber + 1);
}
