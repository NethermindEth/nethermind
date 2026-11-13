// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Prove;
using Nethermind.Eez.Prove.V2;
using SettlementBatch = Nethermind.Eez.Execution.Settlement.PostBatch;

namespace Nethermind.Eez.Attester;

internal sealed class StreamingFinalizer
{
    private readonly AttestationPipeline _pipeline;
    private readonly EezAttestationSigner _signer;
    private readonly Rollup0ParentAuthorizer _authorizer;

    internal StreamingFinalizer(AttestationPipeline pipeline, EezAttestationSigner signer, Rollup0ParentAuthorizer authorizer)
    {
        if (authorizer.Context != pipeline.Context || authorizer.Signer != signer.Address)
            throw new ArgumentException("authorization and signing deployments differ");
        _pipeline = pipeline;
        _signer = signer;
        _authorizer = authorizer;
    }

    internal async Task<Proof> Finalize(SessionOperation operation, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        Finalize finalization = operation.Finalize ?? throw new InvalidOperationException("not a finalization operation");
        ValidateSupportedSettlement(finalization.Settlement);
        ValidateTerminalTime(operation, finalization);
        ValidateProofless(ValidateProfile(finalization, _pipeline.Context));
        L1AuthorizationSnapshot snapshot = await _authorizer.Authorize(operation.Predecessor!, cancellation).ConfigureAwait(false);
        using AdmittedWindow window = ToWindow(operation, finalization);
        ValueHash256 publicInputs = await Task.Run(() => _pipeline.Validate(window, cancellation), CancellationToken.None).ConfigureAwait(false);
        await _authorizer.Recheck(snapshot, cancellation).ConfigureAwait(false);
        return operation.SignCurrent(_signer, publicInputs, cancellation);
    }

    internal static void ValidateSupportedSettlement(SettlementInput? settlement, StreamingSettlementProfile profile = StreamingSettlementProfile.Rollup0Calldata)
    {
        if (settlement is null) throw new RpcException(new Status(StatusCode.InvalidArgument, "settlement input missing"));
        bool conditional = profile == StreamingSettlementProfile.Rollup0Conditional;
        if (conditional) ValidatePostingContext(settlement);
        if (settlement.Blobs.Count != 0)
        {
            if (conditional)
                ValidateBlobSelection(DecodeBatch(settlement), settlement.Blobs.Count);
            throw new RpcException(new Status(StatusCode.Unimplemented, "unsupported settlement profile"));
        }
        if (!conditional) ValidatePostingContext(settlement);
    }

    private static void ValidatePostingContext(SettlementInput settlement)
    {
        if (settlement.PostingContext is { } posting &&
            (posting.ChainId != 0 || posting.ParentNumber != 0 || posting.ParentHash.Length != 32 ||
             new Hash256(posting.ParentHash.Span) == Hash256.Zero || posting.TargetTimestamp == 0))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "invalid Rollup0 parent/time posting context"));
    }

    internal static void ValidateTerminalTime(SessionOperation operation, Finalize finalization)
    {
        if (finalization.Settlement.PostingContext is { } posting && posting.TargetTimestamp != operation.TerminalTimestamp)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Rollup0 posting context must match terminal time"));
    }

    internal static SettlementBatch ValidateProfile(Finalize finalization, EezSettlementContext context,
        StreamingSettlementProfile profile = StreamingSettlementProfile.Rollup0Calldata)
    {
        SettlementBatch batch = DecodeBatch(finalization.Settlement);
        if (profile == StreamingSettlementProfile.Rollup0Conditional)
            ValidateBlobSelection(batch, finalization.Settlement.Blobs.Count);
        if (batch.ProofSystems is [{ } proofSystem] && proofSystem != context.ProofSystem ||
            batch.RollupIdsWithProofSystems is [{ } rollup] && rollup.RollupId != context.RollupId ||
            batch.BlockNumber is not (0 or ulong.MaxValue) || batch.BindMsgSenderInPublicInput)
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "settlement claims differ from configured deployment"));
        foreach (ExecutionEntry entry in batch.Entries)
            if (entry.DestinationRollupId != context.RollupId)
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "settlement entry targets another rollup"));
        L1PostingTarget? target = finalization.Settlement.PostingContext is { } posting
            ? new(posting.TargetTimestamp, new Hash256(posting.ParentHash.Span)) : null;
        try { PostBatchProfile.Validate(batch, context.RollupId, context.ProofSystem, target); }
        catch (EezSettlementException)
        {
            StatusCode code = profile == StreamingSettlementProfile.Rollup0Conditional ? StatusCode.FailedPrecondition : StatusCode.Unimplemented;
            throw new RpcException(new Status(code, "unsupported settlement profile"));
        }
        return batch;
    }

    private static SettlementBatch DecodeBatch(SettlementInput settlement)
    {
        try { return EezCalldata.DecodePostAndVerifyBatch(settlement.AbiCalldata.Span); }
        catch (EezAbiException) { throw new RpcException(new Status(StatusCode.InvalidArgument, "invalid PostBatch calldata")); }
    }

    private static void ValidateBlobSelection(SettlementBatch batch, int count)
    {
        if (batch.BlobIndices.Length != count)
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "blob selection must match submitted blobs"));
        for (int i = 0; i < count; i++)
            if (batch.BlobIndices[i] != (UInt256)(uint)i)
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "blob selection must use submitted order"));
        if (count != 0 && batch.CallData.Length != 0)
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "calldata must be empty beside blobs"));
    }

    private static void ValidateProofless(SettlementBatch batch)
    {
        if (batch.Proofs.Length != batch.ProofSystems.Length)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "proof count must match proof systems"));
        foreach (byte[] proof in batch.Proofs)
            if (proof.Length != 0) throw new RpcException(new Status(StatusCode.InvalidArgument, "PostBatch must be proofless"));
    }

    internal static AdmittedWindow ToWindow(SessionOperation operation, Finalize finalization)
    {
        BlockWitness[] selected = operation.CopyBlocks();
        EezStatelessBlock[] blocks = new EezStatelessBlock[selected.Length];
        (Hash256 Hash, Hash256 ParentHash)[] claims = new (Hash256, Hash256)[selected.Length];
        int initialized = 0;
        try
        {
            for (int i = 0; i < selected.Length; i++)
            {
                claims[i] = (new Hash256(selected[i].Hash.Span), new Hash256(selected[i].ParentHash.Span));
                blocks[i] = new EezStatelessBlock(selected[i].Rlp.ToByteArray(), WindowAssembler.ToWitness(selected[i].Witness));
                initialized++;
            }
            return new AdmittedWindow(finalization.FromBlock, finalization.ToBlock, finalization.Settlement.AbiCalldata.ToByteArray(), blocks, claims)
            {
                L1ParentHash = finalization.Settlement.PostingContext is { } posting ? new Hash256(posting.ParentHash.Span) : null,
                L1TargetTimestamp = finalization.Settlement.PostingContext?.TargetTimestamp,
            };
        }
        catch
        {
            for (int i = 0; i < initialized; i++) blocks[i].Witness.Dispose();
            throw;
        }
    }
}
