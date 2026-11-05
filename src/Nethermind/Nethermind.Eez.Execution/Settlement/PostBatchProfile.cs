// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>
/// The batch shape an attester signs: one rollup verified by one proof system, no static entries, blobs, custom data
/// or sender binding, and immediate entries exactly the leading run of entries without a proxy entry.
/// </summary>
public static class PostBatchProfile
{
    /// <exception cref="EezSettlementException">The batch is outside the supported profile.</exception>
    public static void Validate(PostBatch batch, ulong rollupId, Address proofSystem)
    {
        Require(batch.ProofSystems is [{ } only] && only == proofSystem, $"the batch must be verified by exactly proof system {proofSystem}");
        Require(batch.RollupIdsWithProofSystems is [{ } rollup] && rollup.RollupId == rollupId && rollup.ProofSystemIndexes is [0],
            $"the batch must settle exactly rollup {rollupId} with proof system indexes [0]");
        Require(batch.ExpectedRoots.Length == 0, "expected state roots must be empty");
        for (int i = 0; i < batch.Entries.Length; i++)
        {
            Require(batch.Entries[i].DestinationRollupId == rollupId, $"entry {i} must target rollup {rollupId}");
        }

        Require(batch.StaticEntries.Length == 0, "static entries must be empty");
        Require(batch.ImmediateStaticEntryCount.IsZero, "the immediate static entry count must be zero");
        Require(batch.ImmediateEntryCount == (UInt256)LeadingEntriesWithoutProxy(batch),
            "the immediate entry count must be the leading run of entries without a proxy entry hash");
        Require(batch.BlockNumber == 0, "the block number must be zero");
        Require(batch.BlobIndices.Length == 0, "blob indices must be empty");
        Require(!batch.BindMsgSenderInPublicInput, "the sender must not be bound into the public input");
    }

    /// <summary>The public inputs hash of a batch in the supported profile, for the proof system's verification key.</summary>
    public static ValueHash256 PublicInputsHash(PostBatch batch, ulong rollupId, in ValueHash256 verificationKey)
    {
        ValueHash256[] entryHashes = new ValueHash256[batch.Entries.Length];
        for (int i = 0; i < entryHashes.Length; i++)
        {
            entryHashes[i] = EezCalldata.EntryHash(batch.Entries[i]);
        }

        RollupProofAssignment[] assignments = [new(rollupId, [0], [verificationKey], [])];
        return PublicInputs.Compute(entryHashes, [], [], batch.CallData, assignments, 1, Address.Zero)[0];
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

    private static void Require(bool condition, string rule)
    {
        if (!condition)
        {
            throw new EezSettlementException($"Unsupported batch: {rule}.");
        }
    }
}
