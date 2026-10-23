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
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        SettlementInput snapshot = input.Clone();
        L1PostingContext posting = snapshot.PostingContext ?? throw Invalid();
        if (posting.ChainId == 0 || posting.ChainId != l1Specs.ChainId || posting.TargetTimestamp == 0 || posting.ParentNumber == ulong.MaxValue ||
            posting.ParentHash.Length != Hash256.Size || !posting.ParentHash.Span.ContainsAnyExcept((byte)0)) throw Invalid();
        L1PostingTarget target = new(posting.TargetTimestamp, new Hash256(posting.ParentHash.Span));
        IReleaseSpec spec = l1Specs.GetSpec(posting.ParentNumber + 1, posting.TargetTimestamp);
        int count = snapshot.Blobs.Count;
        if (!spec.IsEip4844Enabled || count == 0 || (ulong)count > Math.Min(spec.MaxBlobCount, spec.MaxBlobsPerTx)) throw Invalid();
        try
        {
            PostBatch batch = EezCalldata.DecodePostAndVerifyBatch(snapshot.AbiCalldata.Span);
            if (batch.Proofs is not ([] or [{ Length: 0 }])) throw Invalid();
            try { RollupXPostBatchProfile.Validate(batch, context.RollupId, context.ProofSystem, count); }
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
            return new RollupXSettlementInput(batch, stream, operations, hashes, posting.ParentNumber, target);
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
