// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// Estimates the serialized size of HSST columns based on snapshot content.
/// Provides conservative estimates with 20% safety margin to ensure buffer allocation is safe.
/// </summary>
internal static class HsstSizeEstimator
{
    private const int TopPathThreshold = 5;
    private const int CompactPathThreshold = 15;

    /// <summary>
    /// Estimates the serialized size of the metadata column.
    /// </summary>
    public static int EstimateMetadataColumnSize() =>
        // Fixed set of 5 entries with small keys/values
        EstimateSimpleHsstSize(5, 5, 5, 32);

    /// <summary>
    /// Estimates the serialized size of the accounts column.
    /// Accounts HSST: Address(20) → Account(RLP, ~100 bytes avg)
    /// </summary>
    public static int EstimateAccountsColumnSize(Snapshot snapshot)
    {
        int accountCount = snapshot.AccountsCount;
        if (accountCount == 0)
            return 2; // Minimal HSST

        int avgAccountRlpSize = 100;
        int avgAddressSeparatorLen = 10; // 20-byte addresses have ~10-byte separators
        return EstimateSimpleHsstSize(accountCount, avgAddressSeparatorLen, avgAddressSeparatorLen, avgAccountRlpSize);
    }

    /// <summary>
    /// Estimates the serialized size of the storage column (3-level nested).
    /// Address(20) → prefix HSST(SlotPrefix(30) → suffix HSST(SlotSuffix(2) → SlotValue))
    /// </summary>
    public static int EstimateStorageColumnSize(Snapshot snapshot)
    {
        int storageCount = 0;
        int distinctAddresses = 0;
        var seenAddresses = new HashSet<Address>();

        foreach (KeyValuePair<(AddressAsKey, UInt256), SlotValue?> kv in snapshot.Storages)
        {
            storageCount++;
            if (seenAddresses.Add(kv.Key.Item1.Value))
                distinctAddresses++;
        }

        if (storageCount == 0)
            return 2; // Minimal HSST

        int slotsPerAddress = storageCount / distinctAddresses;

        // Estimate suffix HSST sizes (SlotSuffix(2) → SlotValue, ~32 bytes avg value)
        // Each distinct prefix group averages ~1 suffix entry; 2-byte keys have ~1-byte separators
        int avgSuffixSeparatorLen = 1;
        int avgSuffixHsstSize = EstimateSimpleHsstSize(slotsPerAddress, avgSuffixSeparatorLen, avgSuffixSeparatorLen, 32);

        // Estimate prefix HSST sizes (SlotPrefix(30) → suffix HSST)
        // Most slots share the same 30-byte prefix per address; estimate ~1 prefix group per address
        int avgPrefixSeparatorLen = 15; // 30-byte prefix keys have ~15-byte separators
        int prefixGroupsPerAddress = Math.Max(1, slotsPerAddress / 4); // conservative estimate
        int avgPrefixHsstSize = EstimateSimpleHsstSize(prefixGroupsPerAddress, avgPrefixSeparatorLen, avgPrefixSeparatorLen, avgSuffixHsstSize);

        int totalPrefixSize = avgPrefixHsstSize * distinctAddresses;
        int totalSuffixSize = avgSuffixHsstSize * distinctAddresses * prefixGroupsPerAddress;

        // Estimate address-level HSST (Address(20) → prefix HSST)
        int avgAddressSeparatorLen = 10;
        return EstimateSimpleHsstSize(distinctAddresses, avgAddressSeparatorLen, avgAddressSeparatorLen, avgPrefixHsstSize)
            + totalPrefixSize + totalSuffixSize;
    }

    /// <summary>
    /// Estimates the serialized size of the self-destruct column.
    /// Self-destruct HSST: Address(20) → bool(1 byte)
    /// </summary>
    public static int EstimateSelfDestructColumnSize(Snapshot snapshot)
    {
        int count = 0;
        foreach (var _ in snapshot.SelfDestructedStorageAddresses)
            count++;

        if (count == 0)
            return 2; // Minimal HSST

        int avgAddressSeparatorLen = 10;
        return EstimateSimpleHsstSize(count, avgAddressSeparatorLen, avgAddressSeparatorLen, 1);
    }

    /// <summary>
    /// Estimates the serialized size of the state top nodes column.
    /// State top nodes HSST: TreePath(3 bytes) → TrieNode(RLP, ~650 bytes avg), path length 0-5
    /// </summary>
    public static int EstimateStateTopNodesColumnSize(Snapshot snapshot)
    {
        int count = 0;
        foreach (KeyValuePair<TreePath, TrieNode> kv in snapshot.StateNodes)
        {
            if (kv.Value.FullRlp.Length > 0 || kv.Value.NodeType != NodeType.Unknown)
            {
                if (kv.Key.Length <= TopPathThreshold)
                    count++;
            }
        }

        if (count == 0)
            return 2; // Minimal HSST

        int avgPathSeparatorLen = 2; // 3-byte top paths have ~2-byte separators
        int avgNodeRlpSize = 650;
        return EstimateSimpleHsstSize(count, avgPathSeparatorLen, avgPathSeparatorLen, avgNodeRlpSize);
    }

    /// <summary>
    /// Estimates the serialized size of the state nodes compact column.
    /// State nodes compact HSST: TreePath(8 bytes) → TrieNode(RLP, ~650 bytes avg), path length 6-15
    /// </summary>
    public static int EstimateStateNodesCompactColumnSize(Snapshot snapshot)
    {
        int count = 0;
        foreach (KeyValuePair<TreePath, TrieNode> kv in snapshot.StateNodes)
        {
            if (kv.Value.FullRlp.Length > 0 || kv.Value.NodeType != NodeType.Unknown)
            {
                if (kv.Key.Length > TopPathThreshold && kv.Key.Length <= CompactPathThreshold)
                    count++;
            }
        }

        if (count == 0)
            return 2; // Minimal HSST

        int avgPathSeparatorLen = 4; // 8-byte compact paths have ~4-byte separators
        int avgNodeRlpSize = 650;
        return EstimateSimpleHsstSize(count, avgPathSeparatorLen, avgPathSeparatorLen, avgNodeRlpSize);
    }

    /// <summary>
    /// Estimates the serialized size of the state nodes fallback column.
    /// State nodes fallback HSST: TreePath(33) → TrieNode(RLP, ~650 bytes avg), path length 16+
    /// </summary>
    public static int EstimateStateNodesFallbackColumnSize(Snapshot snapshot)
    {
        int count = 0;
        foreach (KeyValuePair<TreePath, TrieNode> kv in snapshot.StateNodes)
        {
            if (kv.Value.FullRlp.Length > 0 || kv.Value.NodeType != NodeType.Unknown)
            {
                if (kv.Key.Length > CompactPathThreshold)
                    count++;
            }
        }

        if (count == 0)
            return 2; // Minimal HSST

        int avgPathSeparatorLen = 17; // 33-byte fallback paths have ~17-byte separators
        int avgNodeRlpSize = 650;
        return EstimateSimpleHsstSize(count, avgPathSeparatorLen, avgPathSeparatorLen, avgNodeRlpSize);
    }

    /// <summary>
    /// Estimates the serialized size of the storage nodes compact column (nested).
    /// Outer HSST: Hash256Prefix(20) → inner HSST(TreePath(8) → TrieNode), path length 6-15
    /// </summary>
    public static int EstimateStorageNodesCompactColumnSize(Snapshot snapshot)
    {
        int nodeCount = 0;
        int distinctHashes = 0;
        var seenHashes = new HashSet<Hash256>();

        foreach (KeyValuePair<(Hash256AsKey, TreePath), TrieNode> kv in snapshot.StorageNodes)
        {
            if (kv.Value.FullRlp.Length == 0 && kv.Value.NodeType == NodeType.Unknown)
                continue;
            if (kv.Key.Item2.Length <= CompactPathThreshold)
            {
                nodeCount++;
                if (seenHashes.Add(kv.Key.Item1.Value))
                    distinctHashes++;
            }
        }

        if (nodeCount == 0)
            return 2; // Minimal HSST

        // Estimate inner HSST sizes
        int totalInnerSize = 0;
        int nodesPerHash = nodeCount / distinctHashes;

        int avgPathSeparatorLen = 4; // 8-byte paths have ~4-byte separators
        for (int i = 0; i < distinctHashes; i++)
        {
            totalInnerSize += EstimateSimpleHsstSize(nodesPerHash, avgPathSeparatorLen, avgPathSeparatorLen, 650);
        }

        // Estimate outer HSST (Hash256 prefix 20 bytes → inner HSST)
        int avgHashSeparatorLen = 10; // 20-byte hash prefixes have ~10-byte separators
        int avgOuterValueSize = totalInnerSize / distinctHashes;
        return EstimateSimpleHsstSize(distinctHashes, avgHashSeparatorLen, avgHashSeparatorLen, avgOuterValueSize) + totalInnerSize;
    }

    /// <summary>
    /// Estimates the serialized size of the storage nodes fallback column (nested).
    /// Outer HSST: Hash256Prefix(20) → inner HSST(TreePath(33) → TrieNode), path length 16+
    /// </summary>
    public static int EstimateStorageNodesFallbackColumnSize(Snapshot snapshot)
    {
        int nodeCount = 0;
        int distinctHashes = 0;
        var seenHashes = new HashSet<Hash256>();

        foreach (KeyValuePair<(Hash256AsKey, TreePath), TrieNode> kv in snapshot.StorageNodes)
        {
            if (kv.Value.FullRlp.Length == 0 && kv.Value.NodeType == NodeType.Unknown)
                continue;
            if (kv.Key.Item2.Length > CompactPathThreshold)
            {
                nodeCount++;
                if (seenHashes.Add(kv.Key.Item1.Value))
                    distinctHashes++;
            }
        }

        if (nodeCount == 0)
            return 2; // Minimal HSST

        // Estimate inner HSST sizes
        int totalInnerSize = 0;
        int nodesPerHash = nodeCount / distinctHashes;

        int avgPathSeparatorLen = 17; // 33-byte paths have ~17-byte separators
        for (int i = 0; i < distinctHashes; i++)
        {
            totalInnerSize += EstimateSimpleHsstSize(nodesPerHash, avgPathSeparatorLen, avgPathSeparatorLen, 650);
        }

        // Estimate outer HSST (Hash256 prefix 20 bytes → inner HSST)
        int avgHashSeparatorLen = 10;
        int avgOuterValueSize = totalInnerSize / distinctHashes;
        return EstimateSimpleHsstSize(distinctHashes, avgHashSeparatorLen, avgHashSeparatorLen, avgOuterValueSize) + totalInnerSize;
    }

    /// <summary>
    /// Estimates the size of a simple (single-level) HSST structure.
    /// Formula: DataSize + IndexSize + overhead, with 100% safety margin
    /// </summary>
    internal static int EstimateSimpleHsstSize(
        int entryCount,
        int avgSeparatorLen,
        int avgRemainingKeyLen,
        int avgValueSize)
    {
        if (entryCount == 0)
            return 2; // Minimal HSST (version byte + empty index)

        // Data region: entries with separators and values
        // Each entry has: key(remaining), separator, value length(LEB128), value
        // LEB128 overhead: ~3 bytes for separator length, ~2 bytes for value length
        int avgDataPerEntry = avgValueSize + avgRemainingKeyLen + 5;
        long dataSize = (long)entryCount * avgDataPerEntry;

        // Index region: leaf nodes with separators
        // Number of leaf nodes ≈ (entryCount + 63) / 64 (assuming 64 entries per leaf)
        int leafNodeCount = (entryCount + 63) / 64;

        // Each leaf node has ~64 separators of avgSeparatorLen bytes each, plus overhead
        // Leaf node overhead: ~6 bytes (prefix, count, etc.)
        int avgLeafNodeSize = 6 + 64 * (avgSeparatorLen + 5); // +5 for LEB128 encoding overhead
        long indexSize = (long)leafNodeCount * avgLeafNodeSize;

        // Total with 100% safety margin (very conservative)
        long total = dataSize + indexSize + 2;
        return (int)Math.Min(int.MaxValue, total * 2); // Double for safety
    }

    /// <summary>
    /// Estimates the size of an index region with given number of entries and separator length.
    /// </summary>
    internal static int EstimateIndexRegionSize(int entryCount, int avgSeparatorLen)
    {
        if (entryCount == 0)
            return 0;

        int leafNodeCount = (entryCount + 63) / 64;
        int avgLeafNodeSize = 6 + 64 * (avgSeparatorLen + 5);
        return (int)((long)leafNodeCount * avgLeafNodeSize);
    }
}
