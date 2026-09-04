// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
    IChainHeadInfoProvider chainHeadInfo) : IHandler<Hash256?, InclusionListBytes>
{
    private readonly InclusionListBuilder? _inclusionListBuilder =
        txPool is null ? null : new(txPool, blockTree, specProvider, chainHeadInfo.ReadOnlyStateProvider);

    /// <inheritdoc/>
    /// <param name="parentBlockHash">Block whose header fixes the next-block base fee the candidates are
    /// filtered against; the head when omitted. Nonce readiness stays head-relative.</param>
    public ResultWrapper<InclusionListBytes> Handle(Hash256? parentBlockHash = null)
    {
        if (!specProvider.GetFinalSpec().IsEip7805Enabled)
            return ResultWrapper<InclusionListBytes>.Fail(MergeErrorMessages.UnsupportedFork, MergeErrorCodes.UnsupportedFork);

        BlockHeader? parent = null;
        // The zero hash is how the consensus layer spells "no particular parent", as in ForkchoiceStateV1.
        if (parentBlockHash is not null && parentBlockHash != Hash256.Zero)
        {
            // Failing beats answering for the head: a list built on the wrong parent is silently unappendable.
            parent = blockTree.FindHeader(parentBlockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            if (parent is null)
                return ResultWrapper<InclusionListBytes>.Fail($"Unknown parent block {parentBlockHash}", ErrorCodes.InvalidParams);
        }

        return ResultWrapper<InclusionListBytes>.Success(_inclusionListBuilder?.GetInclusionList(parent) ?? new InclusionListBytes(0));
    }
}
