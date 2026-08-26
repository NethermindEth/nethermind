// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>Builds an inclusion list from pending mempool transactions (EIP-7805).</summary>
/// <remarks>Gated on the final spec, not the head's: a missed slot moves the next block's timestamp, so a
/// narrower gate could refuse the activation slot itself.</remarks>
/// <param name="txPool">Source of the pending transactions; <c>null</c> yields an empty list.</param>
/// <param name="blockTree">Supplies the head header the next block's base fee is derived from.</param>
/// <param name="specProvider">Resolves the fork gate and the base-fee parameters.</param>
/// <param name="chainHeadInfo">
/// Supplies head state. An EIP-8250 keyed transaction heading a sender's pool bucket names a per-key sequence
/// rather than an account nonce, so only the account itself says where that sender's appendable run starts.
/// </param>
public class GetInclusionListTransactionsHandler(
    ITxPool? txPool,
    IBlockTree blockTree,
    ISpecProvider specProvider,
    IChainHeadInfoProvider chainHeadInfo) : IHandler<InclusionListBytes>
{
    private readonly InclusionListBuilder? _inclusionListBuilder =
        txPool is null ? null : new(txPool, blockTree, specProvider, chainHeadInfo.ReadOnlyStateProvider);

    public ResultWrapper<InclusionListBytes> Handle()
        => !specProvider.GetFinalSpec().IsEip7805Enabled
            ? ResultWrapper<InclusionListBytes>.Fail(MergeErrorMessages.UnsupportedFork, MergeErrorCodes.UnsupportedFork)
            : ResultWrapper<InclusionListBytes>.Success(_inclusionListBuilder?.GetInclusionList() ?? new InclusionListBytes(0));
}
