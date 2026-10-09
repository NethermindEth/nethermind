// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.JsonRpc.Modules.IndexProof;

/// <summary>
/// Result of an EIP-8304 index proof query.
/// </summary>
public class IndexProofResult
{
    /// <summary>The binary-encoded index entry (hex).</summary>
    public required byte[] Entry { get; init; }

    /// <summary>The leaf index of the entry in the sorted table.</summary>
    public required int LeafIndex { get; init; }

    /// <summary>The SSZ Merkle proof (sibling hashes from leaf to root, plus length chunk), each as hex.</summary>
    public required byte[][] Proof { get; init; }

    /// <summary>The total number of entries in the table.</summary>
    public required int ListLength { get; init; }

    /// <summary>The table level (0–4).</summary>
    public required int Level { get; init; }

    /// <summary>The first block covered by the table.</summary>
    public required long FirstBlock { get; init; }

    /// <summary>The table size at this level.</summary>
    public required int TableSize { get; init; }

    /// <summary>The storage slot in the index contract where the table root is stored.</summary>
    public required string StorageSlot { get; init; }
}

/// <summary>
/// Storage slot information for an EIP-8304 index table.
/// </summary>
public class StorageSlotInfo
{
    /// <summary>The computed storage slot (hex).</summary>
    public required string Slot { get; init; }

    /// <summary>The table level.</summary>
    public required int Level { get; init; }

    /// <summary>The first block number covered by the table.</summary>
    public required long FirstBlock { get; init; }

    /// <summary>The table size at this level.</summary>
    public required int TableSize { get; init; }
}
