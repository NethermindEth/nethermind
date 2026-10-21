// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Consensus.IndexTables;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.IndexProof;

/// <summary>
/// Serves EIP-8304 index proofs from the in-memory table store.
/// </summary>
public class IndexProofRpcModule(IIndexTableStore store, IBlockTree blockTree) : IIndexProofRpcModule
{
    /// <summary>Maximum number of log proofs returned in a single query to protect against DoS.</summary>
    public const int MaxLogProofs = 1024;

    /// <inheritdoc />
    public ResultWrapper<IndexProofResult?> indexProof_getTransactionProof(Hash256 txHash, long blockNumber, int level = 0)
    {
        if (!TryGetTableParameters(level, blockNumber, out long firstBlock, out int tableSize))
            return ResultWrapper<IndexProofResult?>.Fail("Invalid level or block number", ErrorCodes.InvalidParams);

        IReadOnlyList<IndexEntry>? entries = GetCanonicalTableEntries(level, firstBlock);
        if (entries is null)
            return ResultWrapper<IndexProofResult?>.Fail("Table not found for block", ErrorCodes.ResourceNotFound);

        int leafIndex = FindEntry(entries, IndexEntryType.Transaction, txHash.Bytes);
        if (leafIndex < 0)
            return ResultWrapper<IndexProofResult?>.Fail("Transaction not found in index", ErrorCodes.ResourceNotFound);

        IndexEntryProof proof = IndexProofEngine.GenerateProof(entries, leafIndex);
        return ResultWrapper<IndexProofResult?>.Success(BuildResult(proof, level, firstBlock, tableSize));
    }

    /// <inheritdoc />
    public ResultWrapper<IndexProofResult[]?> indexProof_getLogAddressProofs(Address address, long blockNumber, int level = 0)
    {
        if (!TryGetTableParameters(level, blockNumber, out long firstBlock, out int tableSize))
            return ResultWrapper<IndexProofResult[]?>.Fail("Invalid level or block number", ErrorCodes.InvalidParams);

        IReadOnlyList<IndexEntry>? entries = GetCanonicalTableEntries(level, firstBlock);
        if (entries is null)
            return ResultWrapper<IndexProofResult[]?>.Fail("Table not found for block", ErrorCodes.ResourceNotFound);

        List<int> leafIndices = [];
        int start = FindLowerBound(entries, IndexEntryType.LogAddress, address.Bytes);
        for (int i = start; i < entries.Count && Matches(entries[i], IndexEntryType.LogAddress, address.Bytes); i++)
        {
            leafIndices.Add(i);
            if (leafIndices.Count > MaxLogProofs)
            {
                return ResultWrapper<IndexProofResult[]?>.Fail(
                    $"Query matched {leafIndices.Count} logs, exceeding the limit of {MaxLogProofs} proofs per request.",
                    ErrorCodes.InvalidRequest);
            }
        }

        // One tree build for the whole result set; per-proof generation is quadratic in table size.
        IndexEntryProof[] proofs = IndexProofEngine.GenerateProofs(entries, leafIndices);

        IndexProofResult[] results = new IndexProofResult[proofs.Length];
        for (int i = 0; i < proofs.Length; i++)
        {
            results[i] = BuildResult(proofs[i], level, firstBlock, tableSize);
        }

        return ResultWrapper<IndexProofResult[]?>.Success(results);
    }

    /// <inheritdoc />
    public ResultWrapper<StorageSlotInfo> indexProof_getStorageSlot(int level, long firstBlock)
    {
        if (!TryGetTableParameters(level, firstBlock, out long alignedFirstBlock, out int tableSize))
            return ResultWrapper<StorageSlotInfo>.Fail("Invalid level or block number", ErrorCodes.InvalidParams);

        UInt256 slot = IndexProofEngine.ComputeStorageSlot(tableSize, alignedFirstBlock);

        return ResultWrapper<StorageSlotInfo>.Success(new StorageSlotInfo
        {
            Slot = "0x" + slot.ToString("x"),
            Level = level,
            FirstBlock = alignedFirstBlock,
            TableSize = tableSize,
        });
    }

    private IReadOnlyList<IndexEntry>? GetCanonicalTableEntries(int level, long firstBlock)
    {
        long lookupBlockNumber = level == 0
            ? firstBlock
            : IndexTableMergeScheduler.PublicationBlock(level, firstBlock);

        Hash256? canonicalHash = blockTree.FindCanonicalBlockInfo((ulong)lookupBlockNumber)?.BlockHash
            ?? blockTree.FindHeader((ulong)lookupBlockNumber, BlockTreeLookupOptions.RequireCanonical)?.Hash;

        // The store also holds tables of side branches and locally produced blocks, so only the canonical variant may be served.
        return canonicalHash is null ? null : store.Get(level, firstBlock, canonicalHash);
    }

    /// <summary>
    /// Validates the requested level and maps any block it covers to the table's first block.
    /// </summary>
    /// <remarks>
    /// A level-<c>i</c> table starts at a multiple of <c>TABLE_SIZES[i]</c> and spans
    /// <c>TABLE_SIZES[i]</c> blocks, so a caller naming an arbitrary block within it must be
    /// aligned down before the table can be looked up or its storage slot computed.
    /// </remarks>
    private static bool TryGetTableParameters(int level, long blockNumber, out long firstBlock, out int tableSize)
    {
        firstBlock = 0;
        tableSize = 0;

        if ((uint)level >= (uint)Eip8304Constants.TableSizes.Length || blockNumber < 0)
            return false;

        tableSize = Eip8304Constants.TableSizes[level];
        firstBlock = blockNumber - (blockNumber % tableSize);
        return true;
    }

    private static IndexProofResult BuildResult(IndexEntryProof proof, int level, long firstBlock, int tableSize)
    {
        UInt256 slot = IndexProofEngine.ComputeStorageSlot(tableSize, firstBlock);

        byte[][] proofBytes = new byte[proof.Proof.Length][];
        for (int i = 0; i < proof.Proof.Length; i++)
        {
            proofBytes[i] = new byte[32];
            // SSZ chunks are the little-endian limb layout, not a big-endian number.
            proof.Proof[i].ToLittleEndian(proofBytes[i]);
        }

        return new IndexProofResult
        {
            Entry = proof.Entry,
            LeafIndex = proof.LeafIndex,
            Proof = proofBytes,
            ListLength = proof.ListLength,
            Level = level,
            FirstBlock = firstBlock,
            TableSize = tableSize,
            StorageSlot = "0x" + slot.ToString("x"),
        };
    }

    private static int FindEntry(IReadOnlyList<IndexEntry> entries, IndexEntryType type, ReadOnlySpan<byte> content)
    {
        int index = FindLowerBound(entries, type, content);
        return index < entries.Count && Matches(entries[index], type, content) ? index : -1;
    }

    /// <summary>
    /// Returns the index of the first entry not ordered before (<paramref name="type"/>, <paramref name="content"/>),
    /// or <c>entries.Count</c> if there is none.
    /// </summary>
    /// <remarks>Relies on the table order of <see cref="IndexEntry.CompareTo"/>: type first, then content bytes.</remarks>
    private static int FindLowerBound(IReadOnlyList<IndexEntry> entries, IndexEntryType type, ReadOnlySpan<byte> content)
    {
        int lo = 0, hi = entries.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            IndexEntry entry = entries[mid];
            int cmp = ((ushort)entry.Type).CompareTo((ushort)type);
            if (cmp == 0)
                cmp = entry.ContentBytes.SequenceCompareTo(content);

            if (cmp < 0)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    private static bool Matches(in IndexEntry entry, IndexEntryType type, ReadOnlySpan<byte> content) =>
        entry.Type == type && entry.ContentBytes.SequenceEqual(content);
}
