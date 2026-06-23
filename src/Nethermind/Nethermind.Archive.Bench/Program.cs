// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
//
// Βήμα 4 — go/no-go harness. Runs ON the archive server with the node STOPPED. Opens the state DB
// READ-ONLY (cannot mutate the archive), backfills a sample of state at block N into a temp history
// store, then for the same account hashes: VERIFIES flat read == fresh trie walk (differential gate),
// and MEASURES flat-seek vs trie-walk latency + history size. Args:
//   --datadir <path-to/nethermind_db/mainnet>  --block N  --stateroot 0x..  [--sample 50000] [--scheme halfpath|hash]

using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.History;
using Nethermind.StateComposition.Data;
using Nethermind.StateComposition.Diff;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using RocksDbSharp;

namespace Nethermind.Archive.Bench
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Dictionary<string, string> a = Parse(args);
            bool rangeMode = a.ContainsKey("rootsfile");
            if (!a.TryGetValue("datadir", out string? dataDir) || (!rangeMode && (!a.ContainsKey("block") || !a.ContainsKey("stateroot"))))
            {
                Console.Error.WriteLine("usage: --datadir <db> --statedir <db/state/0> ( --block N --stateroot 0x.. [--sample 50000] | --rootsfile <file> ) [--scheme halfpath|hash]");
                return 1;
            }
            INodeStorage.KeyScheme scheme = a.TryGetValue("scheme", out string? sc) && sc.Equals("hash", StringComparison.OrdinalIgnoreCase)
                ? INodeStorage.KeyScheme.Hash : INodeStorage.KeyScheme.HalfPath;

            // state may be a FullPruningDb (inner numbered subdir, e.g. state/0) → allow explicit overrides
            string stateDir = a.TryGetValue("statedir", out string? sd) ? sd : Path.Combine(dataDir, "state");
            string codeDir = a.TryGetValue("codedir", out string? cd) ? cd : Path.Combine(dataDir, "code");
            string histDir = Path.Combine(Path.GetTempPath(), $"archive-bench-hist-{Environment.ProcessId}");

            ILogManager logs = LimboLogs.Instance;
            RocksDb? stateRocks = null, codeRocks = null, histRocks = null;
            try
            {
                Console.WriteLine($"Opening archive READ-ONLY: {stateDir}");
                DbOptions ro = new();
                stateRocks = RocksDb.OpenReadOnly(ro, stateDir, false);
                codeRocks = RocksDb.OpenReadOnly(ro, codeDir, false);

                RocksKvStore stateKv = new(stateRocks, readOnly: true, "state");
                RocksKvStore codeKv = new(codeRocks, readOnly: true, "code");

                // Account values are EOA-compressed on disk (FullPruningDb wraps the inner db with
                // .WithEOACompressed()). Read through the SAME wrapper so values decompress on read —
                // otherwise we read raw compressed bytes and RLP-decode fails ("got 0").
                IDb stateDb = stateKv.WithEOACompressed();
                RawTrieStore trieStore = new(new NodeStorage(stateDb, scheme));

                // RANGE SIZE experiment: diff consecutive block roots (reusing the production TrieDiffWalker)
                // to measure the trie's marginal bytes-per-change vs the flat store's. This captures the
                // history-accumulation effect (the real source of the archive size gap), which a single-block
                // snapshot cannot show.
                if (a.TryGetValue("rootsfile", out string? rootsPath))
                {
                    RunRangeDiff(trieStore, rootsPath);
                    return 0;
                }

                long block = long.Parse(a["block"]);
                long sample = a.TryGetValue("sample", out string? s) ? long.Parse(s) : 50_000;
                Hash256 root = new(a["stateroot"]);

                StateReader reader = new(trieStore, codeKv, logs);

                BlockHeader header = new(Keccak.Zero, Keccak.OfAnEmptySequenceRlp, Address.Zero, 0, block, 0, 0, [])
                {
                    StateRoot = root
                };

                // DIAGNOSTIC point read — same as the node's eth_getBalance. If THIS works, the reader is
                // fine and the full-trie walk is the issue (path-based historical visiting).
                Address probe = new("0x742d35Cc6634C0532925a3b844Bc454e4438f44e");
                bool gotProbe = reader.TryGetAccount(header, probe, out AccountStruct probeAcc);
                Console.WriteLine($"POINT READ {probe}: found={gotProbe}  balance={(gotProbe ? probeAcc.Balance.ToString() : "-")}");
                Console.WriteLine("  (node's eth_getBalance @23M for this addr = 0x681fad80c009275ac5c)");

                DbOptions wo = new();
                wo.SetCreateIfMissing(true);
                histRocks = RocksDb.Open(wo, histDir);
                RocksKvStore histKv = new(histRocks, readOnly: false, "history");
                RocksDbStateHistory history = new(histKv);

                Console.WriteLine($"Backfilling sample of state at block {block} (root {root})...");
                Stopwatch bsw = Stopwatch.StartNew();
                StateHistoryBackfill backfill = new(history, block, maxLeaves: sample, traverseStorage: false,
                    onProgress: n => Console.Write($"\r  ingested {n:N0} account leaves ({bsw.Elapsed.TotalSeconds:F0}s)..."));
                backfill.Run(reader, header);
                Console.WriteLine();
                Console.WriteLine($"  ingested {backfill.Collected:N0} leaves in {bsw.Elapsed.TotalSeconds:F1}s, MISSING nodes={backfill.MissingNodes:N0}, sampled {backfill.SampledAccountHashes.Count} account hashes");

                List<ValueHash256> probes = backfill.SampledAccountHashes;
                if (probes.Count == 0) { Console.Error.WriteLine("no accounts sampled — empty/wrong root?"); return 2; }

                // trie-side reader by hash
                StateTree trie = new(trieStore.GetTrieStore(null), logs) { RootHash = root };

                // VERIFY: flat read == fresh trie walk, same hash
                int pass = 0, fail = 0;
                foreach (ValueHash256 h in probes)
                {
                    history.TryGetAccountRlpByHash(block, h, out byte[]? flat);
                    ReadOnlySpan<byte> trieVal = trie.Get(h.Bytes, root);
                    bool ok = flat is not null && trieVal.SequenceEqual(flat);
                    if (ok) pass++; else fail++;
                }
                Console.WriteLine($"VERIFY: {pass}/{probes.Count} match, {fail} mismatch  => {(fail == 0 ? "PASS" : "FAIL")}");

                // MEASURE: flat seek vs trie walk on the same hashes
                long sink = 0;
                Stopwatch fsw = Stopwatch.StartNew();
                foreach (ValueHash256 h in probes) { history.TryGetAccountRlpByHash(block, h, out byte[]? v); sink += v?.Length ?? 0; }
                double flatNs = fsw.Elapsed.TotalNanoseconds / probes.Count;

                StateTree trie2 = new(trieStore.GetTrieStore(null), logs) { RootHash = root };
                Stopwatch tsw = Stopwatch.StartNew();
                foreach (ValueHash256 h in probes) { ReadOnlySpan<byte> v = trie2.Get(h.Bytes, root); sink += v.Length; }
                double trieNs = tsw.Elapsed.TotalNanoseconds / probes.Count;
                GC.KeepAlive(sink);

                histRocks.CompactRange((byte[]?)null, (byte[]?)null);   // flush memtable → SST so size is measurable
                long histSize = histKv.SstSizeBytes();
                Console.WriteLine();
                Console.WriteLine($"LATENCY (per account read, {probes.Count} probes):");
                Console.WriteLine($"  flat (1 seek) : {flatNs,9:F0} ns");
                Console.WriteLine($"  trie walk     : {trieNs,9:F0} ns   => {trieNs / flatNs:F2}x");
                Console.WriteLine($"SIZE: history {histSize / 1e6:F1} MB for {backfill.Collected:N0} entries = {(double)histSize / Math.Max(1, backfill.Collected):F1} B/entry");
                Console.WriteLine("NOTE: warm OS cache understates the trie gap; rerun cold for the real multiplier.");
                return fail == 0 ? 0 : 3;
            }
            finally
            {
                stateRocks?.Dispose();
                codeRocks?.Dispose();
                histRocks?.Dispose();
                try { if (Directory.Exists(histDir)) Directory.Delete(histDir, true); } catch { }
            }
        }

        private static void RunRangeDiff(RawTrieStore trieStore, string rootsPath)
        {
            ITrieNodeResolver resolver = trieStore.GetTrieStore(null);
            List<(long blk, Hash256 root)> roots = new();
            foreach (string line in File.ReadAllLines(rootsPath))
            {
                string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                roots.Add((long.Parse(parts[0]), new Hash256(parts[1])));
            }
            if (roots.Count < 2) { Console.Error.WriteLine("rootsfile needs >= 2 lines: '<blockNum> <0xroot>'"); return; }

            TrieDiffWalker walker = new();
            long trieBytes = 0, flatEntries = 0, accAdded = 0, slotsAdded = 0;
            int pairs = 0;
            Stopwatch sw = Stopwatch.StartNew();
            for (int i = 1; i < roots.Count; i++)
            {
                TrieDiff d = walker.ComputeDiff(roots[i - 1].root, roots[i].root, resolver);
                trieBytes += d.AccountTrieBytesAdded + d.StorageTrieBytesAdded;
                flatEntries += d.AccountTrieLeavesAdded + d.StorageTrieLeavesAdded;
                accAdded += d.AccountsAdded; slotsAdded += d.StorageSlotsAdded;
                pairs++;
                if (i % 20 == 0) Console.Write($"\r  diffed {i}/{roots.Count - 1} blocks ({sw.Elapsed.TotalSeconds:F0}s)...");
            }
            Console.WriteLine();

            const double flatBytesPerEntry = 59.1;   // measured on THIS archive (account snapshot run)
            double flatBytes = flatEntries * flatBytesPerEntry;
            double trieBPerEvent = flatEntries > 0 ? (double)trieBytes / flatEntries : 0;
            Console.WriteLine($"RANGE DIFF over {pairs} blocks ({roots[0].blk}..{roots[^1].blk}):");
            Console.WriteLine($"  change events (trie leaves added): {flatEntries:N0}  (accounts +{accAdded:N0}, slots +{slotsAdded:N0})");
            Console.WriteLine($"  TRIE new-node bytes (logical RLP, kept forever): {trieBytes / 1e6:F1} MB = {trieBPerEvent:F0} B/event");
            Console.WriteLine($"  FLAT bytes (events x {flatBytesPerEntry} B measured):           {flatBytes / 1e6:F1} MB = {flatBytesPerEntry} B/event");
            Console.WriteLine($"  => trie archive writes ~{(flatBytes > 0 ? trieBytes / flatBytes : 0):F1}x more bytes than flat for the SAME history");
        }

        private static Dictionary<string, string> Parse(string[] args)
        {
            Dictionary<string, string> d = new(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i].StartsWith("--")) d[args[i][2..]] = args[i + 1];
            return d;
        }
    }
}
