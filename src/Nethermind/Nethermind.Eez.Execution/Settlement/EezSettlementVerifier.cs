// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution.Stateless;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>
/// Decides whether a <c>postAndVerifyBatch</c> may be attested: every claim it makes must follow from the
/// re-executed window, and its DA must publish exactly that window. Only then is the public inputs hash returned.
/// </summary>
public static class EezSettlementVerifier
{
    /// <param name="postBatchCalldata">The <c>postAndVerifyBatch</c> calldata the composer submitted.</param>
    /// <param name="window">
    /// The re-executed window, oldest first. The settling block, the last, must be checkpointed at
    /// <see cref="SettlingBlock.EffectTransactionsOf(Block)"/>.
    /// </param>
    /// <returns>The public inputs hash to sign.</returns>
    /// <exception cref="EezSettlementException">The batch claims something the window does not show.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The context names rollup 0, which is L1.</exception>
    public static ValueHash256 Verify(ReadOnlySpan<byte> postBatchCalldata, IReadOnlyList<EezStatelessBlockResult> window, EezSettlementContext context)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(context.RollupId, EezConstants.L1RollupId);
        if (window.Count == 0)
        {
            throw new EezSettlementException("The window has no blocks.");
        }

        PostBatch batch = Decode(postBatchCalldata);
        PostBatchProfile.Validate(batch, context.RollupId, context.ProofSystem);
        EezStatelessBlockResult settling = window[^1];
        StateUpdate[] updates = StateUpdateChain.Verify(batch, context.RollupId, window[0].Block.Header.ParentHash!.ValueHash256, settling.Hash.ValueHash256);

        SettlingBlock observations = SettlingBlock.Inspect(settling.Block, settling.Receipts, context.RollupId);
        Block[] blocks = new Block[window.Count];
        for (int i = 0; i < window.Count; i++)
        {
            blocks[i] = window[i].Block;
            if (i < window.Count - 1)
            {
                SettlingBlock.EnsureNoEffects(window[i].Block, window[i].Receipts);
            }
        }

        BoundEffect[] effects = EffectBinding.Bind(batch, updates, context.RollupId, settling.Block.Header.ParentHash!.ValueHash256,
            settling.Checkpoints, observations.EffectTransactions, observations.SystemTransactions);
        AuthorizedInbound[] inbound = InboundDelivery.AuthorizeAll(effects, observations.InboundCandidates, context.RollupId);
        AuthorizedOutbound[] outbound = OutboundCall.AuthorizeAll(effects, observations.OutboundEvents, observations.SystemTransactions, context.RollupId);
        DaVerification.Verify(batch.CallData, blocks, outbound, inbound, context.ChainId, context.RollupId);
        return PostBatchProfile.PublicInputsHash(batch, context.RollupId, context.VerificationKey);
    }

    private static PostBatch Decode(ReadOnlySpan<byte> calldata)
    {
        try
        {
            return EezCalldata.DecodePostAndVerifyBatch(calldata);
        }
        catch (EezAbiException e)
        {
            throw new EezSettlementException($"Invalid postAndVerifyBatch calldata: {e.Message}");
        }
    }
}
