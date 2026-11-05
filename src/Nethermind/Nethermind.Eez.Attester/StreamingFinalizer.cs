// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Nethermind.Core.Crypto;
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
        ValidateProofless(ValidateProfile(finalization, _pipeline.Context));
        L1AuthorizationSnapshot snapshot = await _authorizer.Authorize(operation.Predecessor!, cancellation).ConfigureAwait(false);
        using AdmittedWindow window = ToWindow(operation, finalization);
        ValueHash256 publicInputs = await Task.Run(() => _pipeline.Validate(window, cancellation), CancellationToken.None).ConfigureAwait(false);
        await _authorizer.Recheck(snapshot, cancellation).ConfigureAwait(false);
        return operation.SignCurrent(_signer, publicInputs, cancellation);
    }

    internal static void ValidateSupportedSettlement(SettlementInput? settlement)
    {
        if (settlement is null) throw new RpcException(new Status(StatusCode.InvalidArgument, "settlement input missing"));
        if (settlement.Blobs.Count != 0 || settlement.PostingContext is not null)
            throw new RpcException(new Status(StatusCode.Unimplemented, "unsupported settlement profile"));
    }

    internal static SettlementBatch ValidateProfile(Finalize finalization, EezSettlementContext context)
    {
        SettlementBatch batch;
        try { batch = EezCalldata.DecodePostAndVerifyBatch(finalization.Settlement.AbiCalldata.Span); }
        catch (EezAbiException) { throw new RpcException(new Status(StatusCode.InvalidArgument, "invalid PostBatch calldata")); }
        if (batch.ProofSystems is [{ } proofSystem] && proofSystem != context.ProofSystem ||
            batch.RollupIdsWithProofSystems is [{ } rollup] && rollup.RollupId != context.RollupId ||
            batch.BlockNumber != 0 || batch.BindMsgSenderInPublicInput)
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "settlement claims differ from configured deployment"));
        foreach (ExecutionEntry entry in batch.Entries)
            if (entry.DestinationRollupId != context.RollupId)
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "settlement entry targets another rollup"));
        try { PostBatchProfile.Validate(batch, context.RollupId, context.ProofSystem); }
        catch (EezSettlementException) { throw new RpcException(new Status(StatusCode.Unimplemented, "unsupported settlement profile")); }
        return batch;
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
            return new AdmittedWindow(finalization.FromBlock, finalization.ToBlock, finalization.Settlement.AbiCalldata.ToByteArray(), blocks, claims);
        }
        catch
        {
            for (int i = 0; i < initialized; i++) blocks[i].Witness.Dispose();
            throw;
        }
    }
}
