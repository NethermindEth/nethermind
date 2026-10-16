// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Eez.Attester;

/// <summary>Unsigned native execution and settlement checks against the caller's selected predecessor.</summary>
internal sealed class RollupXSettlementExecution(ISpecProvider specs, EezChainRules rules, EezSettlementContext context, ILogManager logs)
{
    private readonly EezStatelessExecutor _executor = new(specs, rules, logs);
    private readonly RollupXObservationReplay _observations = new(specs, rules, logs);

    internal ValueHash256 Validate(AdmittedWindow window, RollupXSettlementInput input, BlockHeader parent,
        Hash256 descendantSeed, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (window.Blocks.Length == 0 || window.Claims.Length != window.Blocks.Length || parent.Number == ulong.MaxValue ||
            window.FromBlock != parent.Number + 1 || window.ToBlock < window.FromBlock ||
            window.ToBlock - window.FromBlock + 1 != (ulong)window.Blocks.Length)
            throw new EezSettlementException("Finalization has no exact selected execution window.");
        Block terminal = Rlp.Decode<Block>(window.Blocks[^1].Rlp) ?? throw new EezSettlementException("Terminal block is missing.");
        RollupXCheckpointPlan plan = RollupXCheckpointPlan.Create(terminal);
        EezStatelessBlockResult[] executed = _executor.Execute(window.Blocks, plan.Positions);
        for (int i = 0; i < executed.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            EezStatelessBlockResult block = executed[i];
            if (block.Block.Number != window.FromBlock + (ulong)i || block.Hash != window.Claims[i].Hash ||
                block.Block.ParentHash != window.Claims[i].ParentHash || block.Block.Header.MixHash != descendantSeed)
                throw new EezSettlementException("Executed window differs from its selected blocks or authorized randomness seed.");
        }
        if (executed[0].Block.ParentHash != parent.Hash || executed[0].PreStateRoot != parent.StateRoot)
            throw new EezSettlementException("Execution does not start from the authorized predecessor state.");
        RollupXObservationClaim[] claims = RollupXSettlementVerifier.Verify(input.Batch, input.Stream, executed, context.RollupId, cancellation);
        _observations.Verify(window.Blocks[^1], executed[^1], claims, cancellation);
        cancellation.ThrowIfCancellationRequested();
        return RollupXPostBatchProfile.PublicInputsHash(input.Batch, context.RollupId, context.VerificationKey, input.BlobHashes, input.Target);
    }
}
