// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>Builds an inclusion list from pending mempool transactions (EIP-7805).</summary>
/// <remarks>Gated on the final spec, not the head's: a missed slot moves the next block's timestamp, so a
/// narrower gate could refuse the activation slot itself.</remarks>
/// <param name="txPool">Source of the pending transactions; <c>null</c> yields an empty list.</param>
/// <param name="blockTree">Supplies the head header the next block's base fee is derived from.</param>
/// <param name="specProvider">Resolves the fork gate and the base-fee parameters.</param>
/// <param name="chainHeadInfo">
/// Supplies head state. The pool admits a sender's bucket on any one entry being ready, so only the account
/// itself says where that sender's appendable run starts.
/// </param>
/// <param name="inclusionListProofs">
/// Chooses the held verified proof an EIP-8288 list attaches; without it the list carries no dependency-bearing
/// transaction and an empty proof.
/// </param>
public class GetInclusionListTransactionsHandler(
    ITxPool? txPool,
    IBlockTree blockTree,
    ISpecProvider specProvider,
    IChainHeadInfoProvider chainHeadInfo,
    InclusionListProofService? inclusionListProofs = null) : IHandler<Hash256?, InclusionListBytes>, IHandler<Hash256?, GetInclusionListV2Result>
{
    private readonly InclusionListBuilder? _inclusionListBuilder =
        txPool is null ? null : new(txPool, blockTree, specProvider, chainHeadInfo.ReadOnlyStateProvider, inclusionListProofs);

    /// <inheritdoc/>
    /// <param name="parentBlockHash">Block whose header fixes the next-block base fee the candidates are
    /// filtered against; the head when omitted. Nonce readiness stays head-relative.</param>
    public ResultWrapper<InclusionListBytes> Handle(Hash256? parentBlockHash = null)
    {
        if (!TryResolveParent(parentBlockHash, out BlockHeader? parent, out ResultWrapper<InclusionListBytes>? failure)) return failure!;
        return ResultWrapper<InclusionListBytes>.Success(_inclusionListBuilder?.GetInclusionList(parent) ?? new InclusionListBytes(0));
    }

    /// <summary>Builds an EIP-8288 <c>InclusionListV1</c>: the transactions <see cref="Handle(Hash256?)"/> draws, then the
    /// dependency-bearing ones a held verified proof covers, with that proof.</summary>
    /// <param name="parentBlockHash">As for <see cref="Handle(Hash256?)"/>; also the block the target fork is predicted from.</param>
    ResultWrapper<GetInclusionListV2Result> IHandler<Hash256?, GetInclusionListV2Result>.Handle(Hash256? parentBlockHash)
    {
        if (!TryResolveParent(parentBlockHash, out BlockHeader? parent, out ResultWrapper<InclusionListBytes>? failure))
            return ResultWrapper<GetInclusionListV2Result>.Fail(failure!.Result.Error!, failure.ErrorCode);
        if (_inclusionListBuilder is null)
            return ResultWrapper<GetInclusionListV2Result>.Success(new GetInclusionListV2Result(new InclusionListBytes(0), RecursiveStarkV1.Empty));
        (InclusionListBytes transactions, RecursiveStarkV1 proof) = _inclusionListBuilder.GetInclusionListWithProof(parent);
        return ResultWrapper<GetInclusionListV2Result>.Success(new GetInclusionListV2Result(transactions, proof));
    }

    private bool TryResolveParent(Hash256? parentBlockHash, out BlockHeader? parent, out ResultWrapper<InclusionListBytes>? failure)
    {
        parent = null;
        failure = null;
        if (!specProvider.GetFinalSpec().IsEip7805Enabled)
        {
            failure = ResultWrapper<InclusionListBytes>.Fail(MergeErrorMessages.UnsupportedFork, MergeErrorCodes.UnsupportedFork);
            return false;
        }

        // The zero hash is how the consensus layer spells "no particular parent", as in ForkchoiceStateV1.
        if (parentBlockHash is not null && parentBlockHash != Hash256.Zero)
        {
            // Failing beats answering for the head: a list built on the wrong parent is silently unappendable.
            parent = blockTree.FindHeader(parentBlockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            if (parent is null)
            {
                failure = ResultWrapper<InclusionListBytes>.Fail($"Unknown parent block {parentBlockHash}", ErrorCodes.InvalidParams);
                return false;
            }
        }
        return true;
    }
}
