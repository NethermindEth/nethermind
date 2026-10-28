// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Google.Protobuf;
using Grpc.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Eez.Prove.V2;

namespace Nethermind.Eez.Attester;

/// <summary>Physical input facts without accepted-parent authorization or semantic settlement validation.</summary>
internal sealed record RollupXSettlementInput(PostBatch Batch, NativeStream Stream, OperationsPayload Operations,
    Hash256[] BlobHashes, ulong L1ParentNumber, L1PostingTarget Target)
{
    internal static RollupXSettlementInput Decode(SettlementInput input, EezSettlementContext context, ISpecProvider l1Specs,
        CancellationToken cancellation, NativeOfflineContext? offline = null, ulong rulesTimestamp = 0, PostBatch? decodedBatch = null, NativeL1Rules? nativeRules = null)
    {
        cancellation.ThrowIfCancellationRequested();
        SettlementInput snapshot = input.Clone();
        ulong parentNumber;
        L1PostingTarget target;
        if (offline is null)
        {
            L1PostingContext posting = snapshot.PostingContext ?? throw Invalid();
            if (posting.ChainId == 0 || posting.ChainId != l1Specs.ChainId || posting.TargetTimestamp == 0 || posting.ParentNumber == ulong.MaxValue ||
                posting.ParentHash.Length != Hash256.Size || !posting.ParentHash.Span.ContainsAnyExcept((byte)0)) throw Invalid();
            parentNumber = posting.ParentNumber;
            target = new(posting.TargetTimestamp, new Hash256(posting.ParentHash.Span));
        }
        else
        {
            parentNumber = offline.Anchor.Number;
            target = new(rulesTimestamp, new Hash256(offline.Anchor.Hash));
        }
        ulong maximum;
        if (offline is { UsesCore: true })
        {
            if (nativeRules is null || nativeRules.Specs.ChainId != l1Specs.ChainId) throw Invalid();
            maximum = nativeRules.PostingBlobLimit(offline.Anchor.TargetTimestamp, parentNumber);
        }
        else
        {
            IReleaseSpec spec = l1Specs.GetSpec(offline?.Anchor.TargetTimestamp is null && offline is not null ? parentNumber : checked(parentNumber + 1), target.Timestamp);
            maximum = spec.IsEip4844Enabled ? Math.Min(spec.MaxBlobCount, spec.MaxBlobsPerTx) : 0;
        }
        int count = snapshot.Blobs.Count;
        if (count == 0 || (ulong)count > maximum) throw Invalid();
        try
        {
            PostBatch batch = decodedBatch ?? EezCalldata.DecodePostAndVerifyBatch(snapshot.AbiCalldata.Span);
            if (batch.Proofs is not ([] or [{ Length: 0 }])) throw Invalid();
            try
            {
                if (offline is null) RollupXPostBatchProfile.Validate(batch, context.RollupId, context.ProofSystem, count);
                else RollupXPostBatchProfile.ValidateNative(batch, context.ProofSystem, count, offline);
            }
            catch (EezSettlementException exception) when (exception.Failure == EezSettlementFailure.Rejected)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "RollupX settlement claims differ from configured profile"));
            }
            foreach (ByteString blob in snapshot.Blobs)
                if (blob.Length != BlobStream.BytesPerBlob) throw Invalid();
            byte[] logical = new byte[checked(count * BlobStream.DataBytesPerBlob)];
            for (int i = 0; i < count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                BlobStream.Unpack(snapshot.Blobs[i].Span, logical.AsSpan(i * BlobStream.DataBytesPerBlob, BlobStream.DataBytesPerBlob));
            }
            NativeStream stream = NativeStream.Decode(logical, context.RollupId);
            OperationsPayload operations = OperationsPayload.Decode(stream.Operations);
            Hash256[] hashes = new Hash256[count];
            for (int i = 0; i < count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                hashes[i] = VersionedHash(snapshot.Blobs[i].Span);
                cancellation.ThrowIfCancellationRequested();
            }
            return new RollupXSettlementInput(batch, stream, operations, hashes, parentNumber, target);
        }
        catch (Exception exception) when (exception is EezAbiException or EezSettlementException or OverflowException or ArgumentException)
        {
            throw Invalid();
        }
        catch (KzgSetupUnavailableException)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, "KZG trusted setup unavailable"));
        }
    }

    private static Hash256 VersionedHash(ReadOnlySpan<byte> blob)
    {
        Span<byte> commitment = stackalloc byte[48];
        Span<byte> hash = stackalloc byte[Hash256.Size];
        KzgPolynomialCommitments.ComputeCommitment(blob, commitment);
        return KzgPolynomialCommitments.TryComputeCommitmentHashV1(commitment, hash)
            ? new Hash256(hash) : throw new InvalidOperationException("KZG commitment hashing failed");
    }

    private static RpcException Invalid() => new(new Status(StatusCode.InvalidArgument, "invalid RollupX settlement input"));
}
