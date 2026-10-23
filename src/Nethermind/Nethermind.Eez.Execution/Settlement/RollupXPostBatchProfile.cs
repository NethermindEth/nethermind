// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>
/// The L1 block a RollupX batch must land in: its timestamp and its parent's hash, which the rollup contract returns as
/// the batch's custom data, so a signature is good for that one L1 slot only.
/// </summary>
public readonly record struct L1PostingTarget(ulong Timestamp, Hash256 ParentHash)
{
    /// <summary><c>abi.encode(uint256 timestamp, bytes32 parentHash)</c>.</summary>
    public byte[] CustomData()
    {
        byte[] data = new byte[2 * AbiWord.Size];
        AbiWord.Write(data.AsSpan(0, AbiWord.Size), Timestamp);
        ParentHash.Bytes.CopyTo(data.AsSpan(AbiWord.Size));
        return data;
    }
}

/// <summary>
/// The batch shape a RollupX attester signs: one rollup verified by one proof system, its stream in every blob of the
/// transaction and none in calldata, the posting block's custom data, no sender binding, static entries that each pin
/// the rollup's root once, and immediate entries exactly the leading run of entries without a proxy entry.
/// </summary>
public static class RollupXPostBatchProfile
{
    /// <summary>The block number a batch carries to read its custom data from the block it lands in.</summary>
    public const ulong CurrentBlock = ulong.MaxValue;

    /// <exception cref="EezSettlementException">The batch is outside the profile.</exception>
    public static void Validate(PostBatch batch, ulong rollupId, Address proofSystem, int blobCount)
    {
        Require(batch.ProofSystems is [{ } only] && only == proofSystem, $"the batch must be verified by exactly proof system {proofSystem}");
        Require(batch.RollupIdsWithProofSystems is [{ } rollup] && rollup.RollupId == rollupId && rollup.ProofSystemIndexes is [0],
            $"the batch must settle exactly rollup {rollupId} with proof system indexes [0]");
        Require(batch.ExpectedRoots.Length == 0, "expected state roots must be empty");
        for (int i = 0; i < batch.Entries.Length; i++)
        {
            Require(batch.Entries[i].DestinationRollupId == rollupId, $"entry {i} must target rollup {rollupId}");
        }

        for (int i = 0; i < batch.StaticEntries.Length; i++)
        {
            StaticExecutionEntry entry = batch.StaticEntries[i];
            Require(entry.DestinationRollupId == rollupId && entry.ExpectedRoots is [{ } pin] && pin.RollupId == rollupId,
                $"static entry {i} must pin rollup {rollupId} once");
        }

        Require(batch.ImmediateStaticEntryCount.IsZero, "the immediate static entry count must be zero");
        Require(batch.ImmediateEntryCount == (UInt256)LeadingEntriesWithoutProxy(batch),
            "the immediate entry count must be the leading run of entries without a proxy entry hash");
        Require(batch.BlockNumber == CurrentBlock, "the block number must read the custom data of the block the batch lands in");
        Require(!batch.BindMsgSenderInPublicInput, "the sender must not be bound into the public input");
        Require(batch.CallData.Length == 0, "the stream must travel in blobs, not calldata", EezSettlementFailure.InvalidDaPayload);
        Require(blobCount > 0 && SelectsEveryBlob(batch.BlobIndices, blobCount), "the batch must select every blob of its transaction once, in order", EezSettlementFailure.InvalidDaPayload);
    }

    /// <summary>The public inputs hash of a batch in the profile, for the proof system's verification key.</summary>
    /// <param name="blobVersionedHashes">The versioned hashes of the transaction's blobs, which the batch selects in order.</param>
    public static ValueHash256 PublicInputsHash(PostBatch batch, ulong rollupId, in ValueHash256 verificationKey, IReadOnlyList<Hash256> blobVersionedHashes,
        L1PostingTarget target)
    {
        ValueHash256[] entryHashes = new ValueHash256[batch.Entries.Length];
        for (int i = 0; i < entryHashes.Length; i++)
        {
            entryHashes[i] = EezCalldata.EntryHash(batch.Entries[i]);
        }

        ValueHash256[] staticEntryHashes = new ValueHash256[batch.StaticEntries.Length];
        for (int i = 0; i < staticEntryHashes.Length; i++)
        {
            staticEntryHashes[i] = EezCalldata.StaticEntryHash(batch.StaticEntries[i]);
        }

        ValueHash256[] blobHashes = new ValueHash256[blobVersionedHashes.Count];
        for (int i = 0; i < blobHashes.Length; i++)
        {
            blobHashes[i] = blobVersionedHashes[i];
        }

        RollupProofAssignment[] assignments = [new(rollupId, [0], [verificationKey], target.CustomData())];
        return PublicInputs.Compute(entryHashes, staticEntryHashes, blobHashes, [], assignments, 1, Address.Zero)[0];
    }

    /// <summary>Whether <paramref name="indices"/> select each of the transaction's <paramref name="blobCount"/> blobs once, in order.</summary>
    public static bool SelectsEveryBlob(UInt256[] indices, int blobCount)
    {
        if (indices.Length != blobCount)
        {
            return false;
        }

        for (int i = 0; i < indices.Length; i++)
        {
            if (indices[i] != (ulong)i)
            {
                return false;
            }
        }

        return true;
    }

    private static int LeadingEntriesWithoutProxy(PostBatch batch)
    {
        int count = 0;
        while (count < batch.Entries.Length && batch.Entries[count].ProxyEntryHash == default)
        {
            count++;
        }

        return count;
    }

    private static void Require(bool condition, string rule, EezSettlementFailure failure = EezSettlementFailure.Rejected)
    {
        if (!condition)
        {
            throw new EezSettlementException(failure, $"Unsupported batch: {rule}.");
        }
    }
}
