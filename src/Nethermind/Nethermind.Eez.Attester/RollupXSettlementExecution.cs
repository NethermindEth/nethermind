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

    internal PreparedOrdinaryBlock Prepare(EezStatelessBlock block, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        EezStatelessBlockResult result = _executor.Execute([block], [])[0];
        cancellation.ThrowIfCancellationRequested();
        return PreparedOrdinaryBlock.Create(result);
    }

    internal ValueHash256 Validate(AdmittedWindow window, RollupXSettlementInput input, BlockHeader parent,
        Hash256 descendantSeed, Hash256 registryRoot, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (window.Blocks.Length == 0 || window.Claims.Length == 0 || parent.Number == ulong.MaxValue ||
            window.FromBlock != parent.Number + 1 || window.ToBlock < window.FromBlock ||
            window.ToBlock - window.FromBlock + 1 != (ulong)window.Claims.Length)
            throw new EezSettlementException("Finalization has no exact selected execution window.");
        Block terminal = Rlp.Decode<Block>(window.Blocks[^1].Rlp) ?? throw new EezSettlementException("Terminal block is missing.");
        RollupXCheckpointPlan plan = RollupXCheckpointPlan.Create(terminal);
        EezStatelessBlockResult[] executed;
        if (window.Prepared.Length == 0)
        {
            if (window.Blocks.Length != window.Claims.Length) throw new EezSettlementException("Witnessed window length differs.");
            executed = _executor.Execute(window.Blocks, plan.Positions);
        }
        else
        {
            if (window.Prepared.Length != window.Claims.Length || window.Prepared[^1] is not null)
                throw new EezSettlementException("Prepared window requires a fresh witnessed terminal block.");
            executed = new EezStatelessBlockResult[window.Claims.Length];
            int fresh = 0;
            for (int i = 0; i < executed.Length; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (window.Prepared[i] is { } prepared) executed[i] = prepared.Materialize();
                else
                {
                    if (fresh == window.Blocks.Length) throw new EezSettlementException("Prepared window is missing a fresh witness.");
                    executed[i] = _executor.Execute([window.Blocks[fresh++]], i == executed.Length - 1 ? plan.Positions : [])[0];
                }
            }
            if (fresh != window.Blocks.Length) throw new EezSettlementException("Prepared window has unused witnesses.");
        }
        for (int i = 0; i < executed.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            EezStatelessBlockResult block = executed[i];
            if (i != 0 && (block.Block.ParentHash != executed[i - 1].Hash || block.PreStateRoot != executed[i - 1].PostStateRoot))
                throw new EezSettlementException("Prepared window hash or state continuity differs.");
            if (block.Block.Number != window.FromBlock + (ulong)i || block.Hash != window.Claims[i].Hash ||
                block.Block.ParentHash != window.Claims[i].ParentHash || block.Block.Header.MixHash != descendantSeed)
                throw new EezSettlementException("Executed window differs from its selected blocks or authorized randomness seed.");
        }
        if (executed[0].Block.ParentHash != parent.Hash || executed[0].PreStateRoot != parent.StateRoot)
            throw new EezSettlementException("Execution does not start from the authorized predecessor state.");
        RollupXObservationClaim[] claims = RollupXSettlementVerifier.Verify(input.Batch, input.Stream, executed, context.RollupId, cancellation);
        _observations.Verify(window.Blocks[^1], executed[^1], claims, cancellation);
        cancellation.ThrowIfCancellationRequested();
        ValidateRegistryRoot(parent, executed[^1], registryRoot);
        return RollupXPostBatchProfile.PublicInputsHash(input.Batch, context.RollupId, context.VerificationKey, input.BlobHashes, input.Target);
    }

    private static void ValidateRegistryRoot(BlockHeader parent, EezStatelessBlockResult terminal, Hash256 registryRoot)
    {
        if (registryRoot == parent.Hash) return;
        foreach (EezTransactionCheckpoint checkpoint in terminal.Checkpoints)
            if (checkpoint.BlockHash == registryRoot) return;
        throw new EezSettlementException("Registry root is not the authenticated parent or a validated terminal checkpoint.");
    }
}
