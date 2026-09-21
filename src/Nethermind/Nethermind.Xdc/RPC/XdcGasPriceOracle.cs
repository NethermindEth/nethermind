// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.TxPool;

namespace Nethermind.Xdc.RPC;

/// <summary>
/// A <see cref="GasPriceOracle"/> that never suggests a price the node's own pool would reject.
/// </summary>
/// <remarks>
/// <see cref="MinGasPriceFilter"/> rejects transactions paying less than
/// <see cref="IXdcReleaseSpec.MinimumGasPrice"/>, but the base oracle floors its estimate on
/// <c>Blocks.MinGasPrice</c>, which the XDC configs deliberately set to zero. Before EIP-1559 there is no
/// base fee to lift the estimate, so <c>eth_gasPrice</c> answers with that zero floor and a caller that
/// follows it gets <c>zero gas price</c> or <c>under min gas price</c> back from the same node.
/// <para>
/// The floor cannot simply be fed to the base constructor instead: it is added to the base fee and scaled
/// by 110%, so once EIP-1559 is live that would suggest roughly twice the minimum rather than the intended
/// margin over it. Clamping the finished estimate keeps the pre-EIP-1559 answer above the floor and leaves
/// the post-EIP-1559 answer, which already clears it, untouched.
/// </para>
/// </remarks>
internal sealed class XdcGasPriceOracle(
    IBlockFinder blockFinder,
    ISpecProvider specProvider,
    IChainHeadInfoProvider chainHeadInfoProvider,
    ILogManager logManager,
    UInt256? minGasPrice = null)
    : GasPriceOracle(blockFinder, specProvider, logManager, minGasPrice)
{
    public override ValueTask<UInt256> GetGasPriceEstimate()
    {
        ValueTask<UInt256> estimate = base.GetGasPriceEstimate();
        UInt256 minimum = NextBlockSpec().MinimumGasPrice;

        return estimate.IsCompletedSuccessfully
            ? ValueTask.FromResult(UInt256.Max(estimate.Result, minimum))
            : ClampAwaited(estimate, minimum);

        static async ValueTask<UInt256> ClampAwaited(ValueTask<UInt256> estimate, UInt256 minimum) =>
            UInt256.Max(await estimate, minimum);
    }

    public override UInt256 GetMaxPriorityGasFeeEstimate()
    {
        UInt256 estimate = base.GetMaxPriorityGasFeeEstimate();
        IXdcReleaseSpec spec = NextBlockSpec();

        // With EIP-1559 the base fee already covers the floor and the tip sits on top of it, so clamping here
        // would demand the minimum twice over. Without it the tip is the whole price and has to clear the floor.
        return spec.IsEip1559Enabled ? estimate : UInt256.Max(estimate, spec.MinimumGasPrice);
    }

    /// <remarks>An estimate is for a transaction that goes into the next block, which is the block
    /// <see cref="MinGasPriceFilter"/> admits against.</remarks>
    private IXdcReleaseSpec NextBlockSpec() => SpecProvider.GetXdcSpec(chainHeadInfoProvider.HeadNumber + 1);
}
