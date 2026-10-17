// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Eez.Follower.RollupX;

/// <summary>
/// Finds what L1 settled for a RollupX rollup. Settling blocks are found as under rollup0. Within one, each batch is
/// credited by <see cref="CarrierAttribution"/>, which also reads the entries L1 skipped from <c>L2TxSkipped</c>; of
/// several anchor-only no-op batches from one root, only the earliest settles, and none when a richer batch from that root
/// settles too, since they would all name the same L2 range. A batch's blobs are read only once it is known to settle
/// us, so the blobs of a batch for another rollup, or of one that failed, never hold the follower back.
/// </summary>
public sealed class RollupXBatchScanner(IEezL1Api l1, IL1BlobSource blobs, Address registry, ulong rollupId, ILogManager logManager) : IL1BatchScanner
{
    public static readonly Hash256 L2TxSkippedTopic = Keccak.Compute("L2TxSkipped(uint256,bytes)");

    private readonly L1BatchScanner _settlingBlocks = new(l1, registry, rollupId, logManager);
    private readonly ILogger _logger = logManager.GetClassLogger<RollupXBatchScanner>();

    public Task<L1SettlingBlock[]> FindSettlingBlocks(ulong fromBlock, ulong toBlock, CancellationToken token) =>
        _settlingBlocks.FindSettlingBlocks(fromBlock, toBlock, token);

    public async Task<ScannedBatch[]> Scan(L1SettlingBlock block, EezL1Block header, CancellationToken token)
    {
        List<Posted> posted = await Batches(block, await SkippedEntries(block, token), token);
        List<L1Batch> batches = new(posted.Count);
        foreach (Posted batch in posted)
        {
            batches.Add(batch.Batch);
        }

        ClaimedChain[] claims = new ClaimedChain[posted.Count];
        for (int i = 0; i < posted.Count; i++) claims[i] = posted[i].Claim;
        (L1Settlement[] settlements, int[] superseded) = RollupXSettlementAttribution.Attribute(batches, claims, block.Roots);
        if (_logger.IsInfo)
        {
            foreach (int index in superseded)
                _logger.Info($"Batch {posted[index].Batch.TransactionHash} in L1 block {block.Number} only re-anchors {claims[index].CurrentRoot}, which another batch settles.");
        }
        ScannedBatch[] result = new ScannedBatch[posted.Count];
        for (int i = 0; i < result.Length; i++)
        {
            L1Batch batch = settlements[i].IsEmpty ? posted[i].Batch : await WithBlobs(posted[i], header, token);
            result[i] = new ScannedBatch(batch, settlements[i]);
        }

        return result;
    }

    /// <summary>The entry indexes L1 skipped, per transaction index of <paramref name="block"/>.</summary>
    private async Task<Dictionary<ulong, HashSet<ulong>>> SkippedEntries(L1SettlingBlock block, CancellationToken token)
    {
        List<EezL1Log> logs = await l1.GetAllLogs(registry, L2TxSkippedTopic, null, block.Number, block.Number, token);
        Dictionary<ulong, HashSet<ulong>> skipped = [];
        foreach (EezL1Log log in logs)
        {
            if (log.BlockHash != block.Hash)
            {
                throw new L1SourceIncompleteException(block.Number, $"The logs of L1 block {block.Number} come from two forks of it.");
            }

            if (log.Topics is not [_, { } index])
            {
                continue;
            }

            UInt256 entry = new(index.Bytes, isBigEndian: true);
            if (entry <= ulong.MaxValue)
            {
                if (!skipped.TryGetValue(log.TransactionIndex, out HashSet<ulong>? entries))
                {
                    skipped[log.TransactionIndex] = entries = [];
                }

                entries.Add((ulong)entry);
            }
        }

        return skipped;
    }

    /// <summary>The direct <c>postAndVerifyBatch</c> calls of <paramref name="block"/>, in transaction order.</summary>
    /// <param name="skipped">The entry indexes L1 skipped, per transaction index.</param>
    private async Task<List<Posted>> Batches(L1SettlingBlock block, Dictionary<ulong, HashSet<ulong>> skipped, CancellationToken token)
    {
        List<Posted> batches = new(block.BatchLogs.Length);
        foreach (EezL1Log log in block.BatchLogs)
        {
            if (batches.Count > 0 && batches[^1].Batch.TransactionHash == log.TransactionHash)
            {
                continue;
            }

            EezL1Transaction transaction = await l1.GetTransactionByBlockHashAndIndex(log.BlockHash, log.TransactionIndex, token)
                ?? throw new L1SourceIncompleteException(log.BlockNumber, $"L1 block {log.BlockHash} has no transaction {log.TransactionIndex}.");
            if (transaction.Hash != log.TransactionHash)
            {
                throw new L1SourceIncompleteException(log.BlockNumber, $"Transaction {log.TransactionIndex} of L1 block {log.BlockHash} is not {log.TransactionHash}: L1 reorganized during the scan.");
            }

            if (Decode(transaction, log) is { } decoded)
            {
                L1Batch batch = L1Batch.Of(decoded, ReadOnlyMemory<byte>.Empty, rollupId, log.BlockNumber, log.BlockHash, log.TransactionHash, log.TransactionIndex);
                ClaimedChain claim = ClaimedChain.Of(decoded, rollupId, skipped.GetValueOrDefault(log.TransactionIndex) ?? []);
                batches.Add(new Posted(batch, decoded, transaction.BlobVersionedHashes ?? [], claim));
            }
        }

        return batches;
    }

    private PostBatch? Decode(EezL1Transaction transaction, EezL1Log log)
    {
        try
        {
            return EezCalldata.DecodePostAndVerifyBatch(transaction.Input);
        }
        catch (EezAbiException e)
        {
            if (_logger.IsDebug) _logger.Debug($"Skipping batch {log.TransactionHash} in L1 block {log.BlockNumber}, which is not a postAndVerifyBatch call: {e.Message}");
            return null;
        }
    }

    /// <summary>The batch with the stream its blobs carry, which must be exactly every blob of its transaction, in order.</summary>
    /// <exception cref="EezFollowerException">The batch settled our rollup without carrying its stream in its blobs.</exception>
    private async Task<L1Batch> WithBlobs(Posted posted, EezL1Block header, CancellationToken token)
    {
        PostBatch decoded = posted.Decoded;
        Hash256[] versionedHashes = posted.VersionedHashes;
        if (decoded.CallData.Length != 0 || versionedHashes.Length == 0 || !RollupXPostBatchProfile.SelectsEveryBlob(decoded.BlobIndices, versionedHashes.Length))
        {
            throw new EezFollowerException(
                $"Batch {posted.Batch.TransactionHash} settled rollup {rollupId} without carrying its stream in every blob of its transaction, in order, and no calldata.");
        }

        byte[][] carried = await blobs.GetBlobs(header, versionedHashes, token);
        try
        {
            return posted.Batch with { DaStream = BlobStream.Unpack(carried) };
        }
        catch (EezSettlementException e)
        {
            throw new EezFollowerException($"Batch {posted.Batch.TransactionHash} settled rollup {rollupId} with blobs that do not unpack: {e.Message}", e);
        }
    }

    private sealed record Posted(L1Batch Batch, PostBatch Decoded, Hash256[] VersionedHashes, ClaimedChain Claim);
}
