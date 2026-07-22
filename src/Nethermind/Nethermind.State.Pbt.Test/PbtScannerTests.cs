// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core.Buffers;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

/// <summary>
/// The scanner reads a tree's shape out of the columns without descending it, so these build a tree
/// of a known shape through the ordinary write path and pin every count against it.
/// </summary>
public class PbtScannerTests
{
    /// <summary>Leaves per stem, chosen so each blob lands in a distinct histogram bucket.</summary>
    private static readonly int[] AccountLeafCounts = [1, 2, 3, 5];
    private static readonly int[] StorageLeafCounts = [1, 4];

    /// <summary>
    /// The tree the tests scan:
    /// <list type="bullet">
    /// <item>four account stems (zone 0) differing in the second nibble, so they share the root's slot
    /// 0 and branch across the boundary of one group at depth 4;</item>
    /// <item>two storage stems (zone 8) agreeing to bit 42, so the run from depth 4 down to depth 40
    /// has a single child throughout and collapses into one chain.</item>
    /// </list>
    /// That gives three groups (depths 0, 4 and 40), one chain and six stems.
    /// </summary>
    private static List<(byte[] Key, byte[]? Value)> BuildWrites()
    {
        List<(byte[], byte[]?)> writes = [];

        for (int stem = 0; stem < AccountLeafCounts.Length; stem++)
        {
            // zone 0, second nibble picks the boundary slot of the depth-4 group
            byte[] prefix = new byte[Stem.Length];
            prefix[0] = (byte)stem;
            AddLeaves(writes, prefix, AccountLeafCounts[stem]);
        }

        for (int stem = 0; stem < StorageLeafCounts.Length; stem++)
        {
            // zone 8, and the only difference is in the nibble at bits 40-43, so everything above it
            // is a single-child run
            byte[] prefix = new byte[Stem.Length];
            prefix[0] = 0x80;
            prefix[5] = (byte)(stem << 4);
            AddLeaves(writes, prefix, StorageLeafCounts[stem]);
        }

        return writes;
    }

    private static void AddLeaves(List<(byte[], byte[]?)> writes, byte[] stem, int leafCount)
    {
        for (int leaf = 0; leaf < leafCount; leaf++)
        {
            byte[] key = new byte[Stem.Length + 1];
            stem.CopyTo(key, 0);
            key[Stem.Length] = (byte)leaf;   // sub-index; the leaves' placement does not affect the counts

            byte[] value = new byte[StemLeafBlob.ValueLength];
            value[0] = (byte)(leaf + 1);     // non-zero, or the write would clear the leaf
            writes.Add((key, value));
        }
    }

    /// <summary>
    /// The shape counts are what the tree holds, not how it is encoded, so every one of them but the
    /// skipped-level count must read the same under either format.
    /// </summary>
    [TestCase(PbtGroupFormat.EveryLevel)]
    [TestCase(PbtGroupFormat.Interleaved)]
    public async Task Scan_CountsTheTreesShape(PbtGroupFormat format)
    {
        PbtScanReport report = await ScanTree(format, BuildWrites(), concurrency: 1);

        Assert.Multiple(() =>
        {
            Assert.That(report.GroupCount, Is.EqualTo(3), "groups at depths 0, 4 and 40");
            Assert.That(report.Root.GroupsByDepth[0], Is.EqualTo(1), "the root group is above every zone");
            Assert.That(report.AccountNodes.GroupsByDepth[4], Is.EqualTo(1), "the account stems' group is in zone 0");
            Assert.That(report.StorageNodes.GroupsByDepth[40], Is.EqualTo(1), "the storage stems' group is in zone 8");
            Assert.That(report.CodeNodes.IsEmpty, Is.True, "nothing was written to the code zone");

            Assert.That(report.StemCount, Is.EqualTo(6), "four account stems and two storage stems");
            Assert.That(report.AccountNodes.StemsByDepth[8], Is.EqualTo(4), "the account stems sit on the depth-4 group's boundary");
            Assert.That(report.StorageNodes.StemsByDepth[44], Is.EqualTo(2), "and the storage stems on the depth-40 group's");

            // every stored node caches its subtree's stem count, so the root's is an independent check
            Assert.That(report.RootSubtreeStemCount, Is.EqualTo(report.StemCount));
            Assert.That(report.StemCountAgrees, Is.True);

            Assert.That(report.Root.GroupBytesByDepth[0], Is.GreaterThan(0), "average size per depth needs the byte totals");

            // the chain rides in the root group's blob, so the three groups are the only keyed entries
            Assert.That(report.TrieNodeBlobCount, Is.EqualTo(3));
            Assert.That(report.TrieNodeKeyBytes, Is.EqualTo(3 * TrieNodeKey.Length));
            Assert.That(report.AccountLeaves.KeyBytes, Is.EqualTo(AccountLeafCounts.Length * Stem.Length));
            Assert.That(report.StorageLeaves.KeyBytes, Is.EqualTo(StorageLeafCounts.Length * Stem.Length));
        });
    }

    /// <summary>
    /// The interleaved encoding leaves an internal node unstored at every odd group-relative level
    /// whose subtree is occupied — four such positions in the root group, three in the account group
    /// and two in the storage group — and the every-level encoding leaves none.
    /// </summary>
    [TestCase(PbtGroupFormat.EveryLevel, 0, 0, 0)]
    [TestCase(PbtGroupFormat.Interleaved, 4, 3, 2)]
    public async Task Scan_CountsInterleavedLevelsPerPartition(PbtGroupFormat format, long root, long account, long storage)
    {
        PbtScanReport report = await ScanTree(format, BuildWrites(), concurrency: 1);

        Assert.Multiple(() =>
        {
            Assert.That(report.Root.InterleaveSkippedNodes, Is.EqualTo(root));
            Assert.That(report.AccountNodes.InterleaveSkippedNodes, Is.EqualTo(account));
            Assert.That(report.StorageNodes.InterleaveSkippedNodes, Is.EqualTo(storage));
            Assert.That(report.InterleaveSkippedNodes, Is.EqualTo(root + account + storage));
            Assert.That(report.Root.InterleavedGroupCount, Is.EqualTo(format == PbtGroupFormat.Interleaved ? 1 : 0));
        });
    }

    /// <summary>
    /// The single-child run below the root's storage slot collapses into one chain, and both the
    /// levels it elides and the entries it saves follow from its span. It is a storage-zone spine, so
    /// none of it lands on any other partition — though the root group, which is in none, holds it.
    /// </summary>
    [TestCase(PbtGroupFormat.EveryLevel)]
    [TestCase(PbtGroupFormat.Interleaved)]
    public async Task Scan_CountsChainsAndWhatTheyElide(PbtGroupFormat format)
    {
        PbtScanReport report = await ScanTree(format, BuildWrites(), concurrency: 1);
        PbtScanReport.TrieNodeStats storage = report.StorageNodes;

        Assert.Multiple(() =>
        {
            Assert.That(report.ChainCount, Is.EqualTo(1));
            Assert.That(storage.ChainCount, Is.EqualTo(1), "the spine is storage's, as chains generally are");
            Assert.That(report.AccountNodes.ChainCount, Is.EqualTo(0));

            Assert.That(storage.ChainsByStartDepth[4], Is.EqualTo(1), "the root group branches, so the run starts below it");
            Assert.That(storage.ChainsBySpan[36], Is.EqualTo(1), "and lands on the group at depth 40");

            // the nodes at depths 5..39: the run's own node is the chain and the target's is the group
            Assert.That(storage.ChainSkippedNodes, Is.EqualTo(35));

            // nine every-level groups would have stored four hashes each; the chain stores two
            Assert.That(storage.ChainEntriesAvoided, Is.EqualTo(9 * PbtLayout.TrieNodeGroupLevelsPerGroup - 2));
            Assert.That(storage.ChainGroupBlobsAvoided, Is.EqualTo(9), "and the chain itself takes no blob to hold");
        });
    }

    /// <summary>
    /// A blob stores its present leaves plus its branching internals, so a stem holding n leaves
    /// contributes n - 1 of them whatever the placement — bar the interleaved layout, which keeps only
    /// those of the 4-, 16- and 64-wide levels: of these stems, whose leaves run from sub-index 0, that
    /// is the one node over <c>[0, 4)</c> wherever a stem holds three leaves or more.
    /// </summary>
    [TestCase(PbtGroupFormat.EveryLevel, 0 + 1 + 2 + 4, 0 + 3)]
    [TestCase(PbtGroupFormat.Interleaved, 0 + 0 + 1 + 1, 0 + 1)]
    public async Task Scan_CountsLeafBlobsPerColumn(PbtGroupFormat format, int accountIntermediates, int storageIntermediates)
    {
        PbtScanReport report = await ScanTree(format, BuildWrites(), concurrency: 1);
        long interleavedAccountBlobs = format == PbtGroupFormat.Interleaved ? AccountLeafCounts.Length : 0;

        Assert.Multiple(() =>
        {
            Assert.That(report.AccountLeaves.BlobCount, Is.EqualTo(AccountLeafCounts.Length));
            Assert.That(report.AccountLeaves.LeafCount, Is.EqualTo(1 + 2 + 3 + 5));
            Assert.That(report.AccountLeaves.IntermediateNodeCount, Is.EqualTo(accountIntermediates));
            Assert.That(report.AccountLeaves.InterleavedBlobCount, Is.EqualTo(interleavedAccountBlobs), "the one setting picks the leaf layout too");
            foreach (int leaves in AccountLeafCounts)
            {
                Assert.That(report.AccountLeaves.BlobsByLeafCount[leaves], Is.EqualTo(1), $"one account blob holds {leaves} leaves");
            }

            Assert.That(report.StorageLeaves.BlobCount, Is.EqualTo(StorageLeafCounts.Length));
            Assert.That(report.StorageLeaves.LeafCount, Is.EqualTo(1 + 4));
            Assert.That(report.StorageLeaves.IntermediateNodeCount, Is.EqualTo(storageIntermediates));
            Assert.That(report.StorageLeaves.BlobsByLeafCount[1], Is.EqualTo(1), "one storage blob holds a single slot");
            Assert.That(report.StorageLeaves.BlobsByLeafCount[4], Is.EqualTo(1), "and one holds four");

            Assert.That(report.CodeLeaves.BlobCount, Is.EqualTo(0), "no code overflows into its own zone here");
            Assert.That(report.AccountLeaves.LegacyBlobCount, Is.EqualTo(0), "the updater only ever writes the current layout");
        });
    }

    /// <summary>
    /// A blob holding its children counts as the nodes it holds, at the depths they sit at, so the
    /// shape reads exactly as it would had they been stored apart — and the sharing itself is counted
    /// beside it, one lookup and one key saved per child.
    /// </summary>
    [TestCase(PbtGroupFormat.EveryLevel)]
    [TestCase(PbtGroupFormat.Interleaved)]
    public async Task Scan_ReadsAClusterAsTheNodesItHolds(PbtGroupFormat format)
    {
        // three account stems: two parting at nibble 2, so their group at depth 8 hangs under the
        // depth-4 group that the third one branches — and depth 4 holds its children
        List<(byte[], byte[]?)> writes = [];
        foreach (byte[] prefix in (byte[][])[[0x00, 0x00], [0x00, 0x10], [0x01, 0x00]])
        {
            byte[] stem = new byte[Stem.Length];
            prefix.CopyTo(stem, 0);
            AddLeaves(writes, stem, 1);
        }

        PbtScanReport report = await ScanTree(format, writes, concurrency: 1);

        Assert.Multiple(() =>
        {
            Assert.That(report.GroupCount, Is.EqualTo(3), "groups at depths 0, 4 and 8");
            Assert.That(report.AccountNodes.GroupsByDepth[4], Is.EqualTo(1));
            Assert.That(report.AccountNodes.GroupsByDepth[8], Is.EqualTo(1), "the clustered child is counted at its own depth");
            Assert.That(report.ClusterCount, Is.EqualTo(1), "the depth-4 blob holds the depth-8 group");
            Assert.That(report.ClusteredGroupCount, Is.EqualTo(1));
            Assert.That(report.TrieNodeBlobCount, Is.EqualTo(2), "the clustered child is stored under no key of its own");
            Assert.That(report.AccountNodes.KeyBytes, Is.EqualTo(TrieNodeKey.Length), "so zone 0 pays for the depth-4 blob alone");
            Assert.That(report.AccountNodes.ClustersByDepth[4], Is.EqualTo(1), "a cluster counts at the depth of the group it holds");
            Assert.That(report.AccountNodes.ClusteredGroupsByDepth[4], Is.EqualTo(1), "and its children beside it, not at their own depth");
            Assert.That(report.ChainCount, Is.EqualTo(0), "nothing here collapses, so the blob is its two groups and the framing");
            Assert.That(
                report.AccountNodes.ClusterBytesByDepth[4],
                Is.EqualTo(report.AccountNodes.GroupBytesByDepth[4] + report.AccountNodes.GroupBytesByDepth[8] + report.AccountNodes.ClusterFramingBytes),
                "the cluster's size is the whole stored value, child blob and framing included");
            Assert.That(report.StemCount, Is.EqualTo(3));
            Assert.That(report.StemCountAgrees, Is.True);
            Assert.That(report.Format(), Does.Contain("Clusters by depth"));
            Assert.That(report.Format(), Does.Contain("Cluster blobs by depth"));
        });
    }

    [Test]
    public async Task Scan_EmptyDatabase_CountsNothing()
    {
        PbtScanReport report = await new PbtScanner(new SnapshotableMemColumnsDb<PbtColumns>("pbt"), new PbtConfig(), LimboLogs.Instance).Scan(default);

        Assert.Multiple(() =>
        {
            Assert.That(report.GroupCount, Is.EqualTo(0));
            Assert.That(report.ChainCount, Is.EqualTo(0));
            Assert.That(report.StemCount, Is.EqualTo(0));
            Assert.That(report.AccountLeaves.BlobCount, Is.EqualTo(0));
            Assert.That(report.StemCountAgrees, Is.True, "nothing counted matches nothing recorded");
        });
    }

    [Test]
    public async Task Format_ReportsEachPartitionThatHoldsAnything()
    {
        string report = (await ScanTree(PbtGroupFormat.Interleaved, BuildWrites(), concurrency: 1)).Format();

        Assert.Multiple(() =>
        {
            Assert.That(report, Does.Contain("--- Root ---"));
            Assert.That(report, Does.Contain("--- Account ---"));
            Assert.That(report, Does.Contain("--- Storage ---"));
            Assert.That(report, Does.Not.Contain("--- Code ---"), "an empty partition is left out");
            Assert.That(report, Does.Contain("Account leaf blobs"));
            Assert.That(report, Does.Contain("Storage leaf blobs"));
            Assert.That(report, Does.Not.Contain("Clusters by depth"), "no group of this tree holds its children");
        });
    }

    /// <summary>Stems per zone in the wide tree, enough to spread over several shards of every column.</summary>
    private const int WideStemCount = 64;

    /// <summary>
    /// A tree wide enough to straddle the sweep's shard boundaries: its stems differ in the leading two
    /// bytes the leaf ranges are cut on, and branch high enough to fill more than one trie node depth.
    /// </summary>
    private static List<(byte[] Key, byte[]? Value)> BuildWideWrites()
    {
        List<(byte[], byte[]?)> writes = [];

        for (int stem = 0; stem < WideStemCount; stem++)
        {
            byte[] account = new byte[Stem.Length];
            account[0] = (byte)(stem & 0x0F);            // zone 0
            account[1] = (byte)(stem << 2);
            AddLeaves(writes, account, 1 + stem % 4);

            byte[] storage = new byte[Stem.Length];
            storage[0] = (byte)(0x80 | (stem & 0x0F));   // zone 8
            storage[1] = (byte)(stem << 2);
            AddLeaves(writes, storage, 1 + stem % 3);
        }

        return writes;
    }

    /// <summary>
    /// Sharding the sweep may not change what it counts. The absolute counts are what catch a range
    /// boundary dropping or double counting the entries around it; the rendered report then pins every
    /// remaining scalar and histogram bucket against the serial sweep in one comparison.
    /// </summary>
    [TestCase(2)]
    [TestCase(8)]
    public async Task Scan_ShardedSweepCountsWhatTheSerialOneDoes(int concurrency)
    {
        List<(byte[] Key, byte[]? Value)> writes = BuildWideWrites();
        string serial = (await ScanTree(PbtGroupFormat.Interleaved, writes, concurrency: 1)).Format();
        PbtScanReport sharded = await ScanTree(PbtGroupFormat.Interleaved, writes, concurrency);

        Assert.Multiple(() =>
        {
            Assert.That(sharded.StemCount, Is.EqualTo(2 * WideStemCount), "the trie node ranges cover every stem exactly once");
            Assert.That(sharded.AccountLeaves.BlobCount, Is.EqualTo(WideStemCount), "the leaf ranges cover every account blob exactly once");
            Assert.That(sharded.StorageLeaves.BlobCount, Is.EqualTo(WideStemCount), "the leaf ranges cover every storage blob exactly once");
            Assert.That(sharded.StemCountAgrees, Is.True);
            Assert.That(sharded.Format(), Is.EqualTo(serial));
        });
    }

    private static Task<PbtScanReport> ScanTree(PbtGroupFormat format, List<(byte[] Key, byte[]? Value)> writes, int concurrency)
    {
        PbtTreeHarness harness = new(PooledRefCountingMemoryProvider.Instance, format);
        harness.ApplyBatch(writes);

        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        foreach (KeyValuePair<TrieNodeKey, byte[]> node in harness.Nodes)
        {
            db.GetColumnDb(TrieNodeColumn(node.Key))[node.Key.ToDbKey()] = node.Value;
        }

        foreach (KeyValuePair<Stem, byte[]> blob in harness.Blobs)
        {
            db.GetColumnDb(LeafColumn(blob.Key))[blob.Key.Bytes.ToArray()] = blob.Value;
        }

        return new PbtScanner(db, new PbtConfig { ScanTreeConcurrency = concurrency }, LimboLogs.Instance).Scan(default);
    }

    /// <summary>Routes a stem to its leaf column by zone, as the persistence layer does.</summary>
    private static PbtColumns LeafColumn(in Stem stem) => stem.Zone switch
    {
        0x0 => PbtColumns.AccountLeaves,
        0x1 => PbtColumns.CodeLeaves,
        _ => PbtColumns.StorageLeaves,
    };

    /// <summary>Routes a trie node to its column by zone, as the persistence layer does.</summary>
    private static PbtColumns TrieNodeColumn(in TrieNodeKey key) => key.Path.Zone switch
    {
        0x0 => PbtColumns.AccountTrieNodes,
        0x1 => PbtColumns.CodeTrieNodes,
        _ => PbtColumns.StorageTrieNodes,
    };
}
