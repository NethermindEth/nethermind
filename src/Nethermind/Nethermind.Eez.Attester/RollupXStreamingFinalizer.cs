// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Eez.Prove.V2;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Block = Nethermind.Core.Block;

namespace Nethermind.Eez.Attester;

internal sealed class RollupXStreamingFinalizer
{
    private readonly ISpecProvider _l1Specs;
    private readonly EezChainRules _rules;
    private readonly EezSettlementContext _context;
    private readonly EezAttestationSigner _signer;
    private readonly RollupXParentAuthorizer _authorizer;
    private readonly EezStatelessExecutor _executor;
    private readonly RollupXObservationReplay _observations;

    internal RollupXStreamingFinalizer(ISpecProvider l1Specs, ISpecProvider l2Specs, EezChainRules rules,
        EezSettlementContext context, EezAttestationSigner signer, RollupXParentAuthorizer authorizer, ILogManager logs)
    {
        if (rules.Schedule is not { } schedule || schedule.L1Reference.ChainId != l1Specs.ChainId || context.ChainId != l2Specs.ChainId)
            throw new ArgumentException("finalization requires matching RollupX chain identities");
        _l1Specs = l1Specs;
        _rules = rules;
        _context = context;
        _signer = signer;
        _authorizer = authorizer;
        _executor = new EezStatelessExecutor(l2Specs, rules, logs);
        _observations = new RollupXObservationReplay(l2Specs, rules, logs);
    }

    internal async Task<Proof> Finalize(SessionOperation operation, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        Finalize selection = operation.Finalize ?? throw new InvalidOperationException("not a finalization operation");
        ValidateSupportedSettlement(selection.Settlement);
        RollupXSettlementInput input = RollupXSettlementInput.Decode(selection.Settlement, _context, _l1Specs, cancellation);
        RollupXAuthorization authorization = await _authorizer.Authorize(operation.Predecessor!, selection.Settlement.PostingContext, cancellation).ConfigureAwait(false);
        if (authorization.Context != _context || authorization.Signer != _signer.Address ||
            authorization.Target != input.Target || authorization.L1ParentNumber != input.L1ParentNumber)
            throw RollupXAuthorizationEvidence.Rejected();
        using AdmittedWindow window = StreamingFinalizer.ToWindow(operation, selection);
        ValueHash256 publicInputs = await Task.Run(() => Validate(window, input, authorization, cancellation), CancellationToken.None).ConfigureAwait(false);
        await _authorizer.Recheck(authorization, cancellation).ConfigureAwait(false);
        return operation.SignCurrent(_signer, publicInputs, cancellation);
    }

    internal static void ValidateSupportedSettlement(SettlementInput? settlement)
    {
        if (settlement is null || settlement.PostingContext is null || settlement.Blobs.Count == 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "RollupX settlement input missing"));
    }

    private ValueHash256 Validate(AdmittedWindow window, RollupXSettlementInput input, RollupXAuthorization authorization,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        try
        {
            if (window.Blocks.Length == 0 || window.Claims.Length != window.Blocks.Length)
                throw new EezSettlementException("Finalization has no exact claimed execution window.");
            BlockHeader parent = Rlp.Decode<BlockHeader>(authorization.ParentHeader.ToByteArray())
                ?? throw new EezSettlementException("Authorized predecessor header is missing.");
            ValidateRange(window, input, authorization, parent);
            Block terminal = Rlp.Decode<Block>(window.Blocks[^1].Rlp) ?? throw new EezSettlementException("Terminal block is missing.");
            RollupXCheckpointPlan plan = RollupXCheckpointPlan.Create(terminal);
            EezStatelessBlockResult[] executed = _executor.Execute(window.Blocks, plan.Positions);
            for (int i = 0; i < executed.Length; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                EezStatelessBlockResult block = executed[i];
                if (block.Block.Number != window.FromBlock + (ulong)i || block.Hash != window.Claims[i].Hash ||
                    block.Block.ParentHash != window.Claims[i].ParentHash || block.Block.Header.MixHash != authorization.DescendantSeed)
                    throw new EezSettlementException("Executed window differs from its selected blocks or authorized randomness seed.");
            }
            if (executed[0].Block.ParentHash != parent.Hash || executed[0].PreStateRoot != parent.StateRoot)
                throw new EezSettlementException("Execution does not start from the authorized predecessor state.");
            RollupXObservationClaim[] claims = RollupXSettlementVerifier.Verify(input.Batch, input.Stream, executed, _context.RollupId, cancellation);
            _observations.Verify(window.Blocks[^1], executed[^1], claims, cancellation);
            cancellation.ThrowIfCancellationRequested();
            return RollupXPostBatchProfile.PublicInputsHash(input.Batch, _context.RollupId, _context.VerificationKey, input.BlobHashes, input.Target);
        }
        catch (Exception exception) when (exception is EezSettlementException or EezStatelessException or WitnessValidationException or RlpException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "RollupX settlement validation rejected"));
        }
    }

    private void ValidateRange(AdmittedWindow window, RollupXSettlementInput input, RollupXAuthorization authorization, BlockHeader parent)
    {
        RollupXSchedule schedule = _rules.Schedule!;
        if (parent.Number == ulong.MaxValue || window.FromBlock != parent.Number + 1 || window.ToBlock < window.FromBlock ||
            window.ToBlock - window.FromBlock + 1 != (ulong)window.Blocks.Length || !schedule.IsSyncPosition(parent.Number) ||
            schedule.AnchorKind(window.ToBlock, input.Target.Timestamp) is not { } kind)
            throw new EezSettlementException("Settlement range is outside the authenticated Sync grid.");
        if (kind == RollupXAnchorKind.CatchUp && input.Stream.Messages.Length != 0)
            throw new EezSettlementException("Catch-up settlement carries cross-chain actions.");
        if (authorization.RegistryRoot != parent.Hash)
            throw new EezSettlementException("Registry root needs independently validated consumed-prefix evidence.");
    }
}
