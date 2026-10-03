// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.IndexTables;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.IndexProof;

/// <summary>
/// Serves EIP-8304 index proofs from the in-memory table store.
/// </summary>
public class IndexProofRpcModule(IIndexTableStore store, IBlockTree? blockTree = null) : IIndexProofRpcModule
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

        int typeStart = FindTypeStart(entries, IndexEntryType.LogAddress);
        List<int> leafIndices = [];
        for (int i = typeStart; i < entries.Count && entries[i].Type == IndexEntryType.LogAddress; i++)
        {
            if (ContentEquals(entries[i], address.Bytes))
            {
                leafIndices.Add(i);
                if (leafIndices.Count > MaxLogProofs)
                {
                    return ResultWrapper<IndexProofResult[]?>.Fail(
                        $"Query matched {leafIndices.Count} logs, exceeding the limit of {MaxLogProofs} proofs per request.",
                        ErrorCodes.InvalidRequest);
                }
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
        if (blockTree is not null)
        {
            long lookupBlockNumber = level == 0
                ? firstBlock
                : IndexTableMergeScheduler.PublicationBlock(level, firstBlock);

            Hash256? canonicalHash = blockTree.FindCanonicalBlockInfo((ulong)lookupBlockNumber)?.BlockHash
                ?? blockTree.FindHeader((ulong)lookupBlockNumber, BlockTreeLookupOptions.RequireCanonical)?.Hash;

            if (canonicalHash is not null)
            {
                return store.Get(level, firstBlock, canonicalHash);
            }
        }

        return store.Get(level, firstBlock);
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
        int typeStart = FindTypeStart(entries, type);
        for (int i = typeStart; i < entries.Count && entries[i].Type == type; i++)
        {
            if (ContentEquals(entries[i], content))
                return i;
        }

        return -1;
    }

    private static int FindTypeStart(IReadOnlyList<IndexEntry> entries, IndexEntryType type)
    {
        int lo = 0, hi = entries.Count - 1;
        int typeStart = entries.Count;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (entries[mid].Type < type)
                lo = mid + 1;
            else
            {
                typeStart = mid;
                hi = mid - 1;
            }
        }

        return typeStart;
    }

    /// <summary>
    /// Tests an entry's content field against <paramref name="content"/>.
    /// </summary>
    private static bool ContentEquals(in IndexEntry entry, ReadOnlySpan<byte> content) =>
        entry.ContentBytes.SequenceEqual(content);
}
