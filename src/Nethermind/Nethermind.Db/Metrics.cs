// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;

namespace Nethermind.Db
{
    public static class Metrics
    {
        [Description("Number of Bloom DB reads.")]
        public static long BloomDbReads { get; set; }

        [Description("Number of Bloom DB writes.")]
        public static long BloomDbWrites { get; set; }

        [Description("Number of CHT DB reads.")]
        public static long CHTDbReads { get; set; }

        [Description("Number of CHT DB writes.")]
        public static long CHTDbWrites { get; set; }

        [Description("Number of Blocks DB reads.")]
        public static long BlocksDbReads { get; set; }

        [Description("Number of Blocks DB writes.")]
        public static long BlocksDbWrites { get; set; }

        [Description("Number of Code DB reads.")]
        public static long CodeDbReads { get; set; }

        [Description("Number of Code DB writes.")]
        public static long CodeDbWrites { get; set; }

        [Description("Number of Receipts DB reads.")]
        public static long ReceiptsDbReads { get; set; }

        [Description("Number of Receipts DB writes.")]
        public static long ReceiptsDbWrites { get; set; }

        [Description("Number of Block Infos DB reads.")]
        public static long BlockInfosDbReads { get; set; }

        [Description("Number of Block Infos DB writes.")]
        public static long BlockInfosDbWrites { get; set; }

        [Description("Number of State Trie reads.")]
        public static long StateTreeReads { get; set; }

        [Description("Number of Blocks Trie writes.")]
        public static long StateTreeWrites { get; set; }

        [Description("Number of State DB reads.")]
        public static long StateDbReads { get; set; }

        [Description("Number of State DB writes.")]
        public static long StateDbWrites { get; set; }

        [Description("Number of State DB duplicate writes during full pruning.")]
        public static int StateDbInPruningWrites;

        [Description("Number of storge trie reads.")]
        public static long StorageTreeReads { get; set; }

        [Description("Number of storage trie writes.")]
        public static long StorageTreeWrites { get; set; }

        [Description("Number of other DB reads.")]
        public static long OtherDbReads { get; set; }

        [Description("Number of other DB writes.")]
        public static long OtherDbWrites { get; set; }

        [Description("Number of Headers DB reads.")]
        public static long HeaderDbReads { get; set; }

        [Description("Number of Headers DB writes.")]
        public static long HeaderDbWrites { get; set; }

        [Description("Number of Witness DB reads.")]
        public static long WitnessDbReads { get; set; }

        [Description("Number of Witness DB writes.")]
        public static long WitnessDbWrites { get; set; }

        [Description("Number of Metadata DB reads.")]
        public static long MetadataDbReads { get; set; }

        [Description("Number of Metadata DB writes.")]
        public static long MetadataDbWrites { get; set; }

        [Description("Indicator if StadeDb is being pruned.")]
        public static int StateDbPruning { get; set; }

        [Description("Metrics extracted from RocksDB Compacion Stats and DB Statistics")]
        public static IDictionary<string, long> DbStats { get; set; } = new ConcurrentDictionary<string, long>();
    }
}
