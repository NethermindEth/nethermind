// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.Test.P2P;

[Explicit("Measures the check on a real RocksDB table holding a full window; run it by name and read the output")]
public class DataColumnSidecarPoolStoredRangeRocksDbTests
{
    private const ulong First = 13_399_995;
    private const ulong Window = Eip7594DasConstants.MinEpochsForDataColumnSidecarsRequests * 32;
    private const ulong Last = First + Window - 1;
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;

    private static Hash256 RootAt(ulong slot) => Keccak.Compute($"canonical {slot}");

    private static ColumnsDb<BeaconChainDbColumns> Open(string path) =>
        new(path, new DbSettings("beaconChain", path), new DbConfig(),
            new RocksDbConfigFactory(new DbConfig(), new PruningConfig(), new TestHardwareInfo(), LimboLogs.Instance, validateConfig: false),
            LimboLogs.Instance, Enum.GetValues<BeaconChainDbColumns>());

    [Test]
    public void The_stored_range_check_of_a_full_window_on_RocksDB()
    {
        string path = Path.Combine(Path.GetTempPath(), "bc-stored-range-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            IReadOnlyList<ulong> sampled = new NodeColumnCustody(TestItem.PrivateKeyA.PublicKey.Hash, Eip7594DasConstants.CustodyRequirement).SampledColumns;
            UInt128 required = UInt128.Zero;
            foreach (ulong column in sampled)
            {
                required |= UInt128.One << (int)column;
            }

            Stopwatch build = Stopwatch.StartNew();
            using (ColumnsDb<BeaconChainDbColumns> db = Open(path))
            {
                BeaconChainStore store = new(db, Spec);
                store.PutMetadata(BeaconDiscovery.IdentityMetadataKey, TestItem.PrivateKeyA.KeyBytes);
                DataColumnSidecarGloas[] templates = new DataColumnSidecarGloas[sampled.Count];
                for (int i = 0; i < templates.Length; i++)
                {
                    templates[i] = DataColumnSidecarGloasTestFixture.BuildSidecar(sampled[i]);
                }

                List<(ulong Slot, Hash256? Root)> canonical = new(4096);
                for (ulong slot = First; slot <= Last; slot++)
                {
                    if (slot % 10 != 7)
                    {
                        Hash256 root = RootAt(slot);
                        canonical.Add((slot, root));
                        if (slot % 5 == 0)
                        {
                            store.PutBlock(root, TestChain.CreateBlock(slot, Hash256.Zero));
                        }
                        else
                        {
                            foreach (DataColumnSidecarGloas template in templates)
                            {
                                template.Slot = slot;
                                template.BeaconBlockRoot = root;
                                store.PutDataColumnSidecar(template);
                            }
                        }
                    }

                    if (canonical.Count == 4096 || slot == Last)
                    {
                        store.ApplyCanonicalIndexChanges(canonical, slot);
                        canonical.Clear();
                    }
                }

                store.RaiseDataColumnFloor(First);
                db.Flush();
            }

            build.Stop();

            using ColumnsDb<BeaconChainDbColumns> reopened = Open(path);
            BeaconChainStore reopenedStore = new(reopened, Spec);
            ulong top = reopenedStore.GetCanonicalIndexTopSlot() ?? 0;

            string[] laps = new string[3];
            for (int lap = 0; lap < laps.Length; lap++)
            {
                Stopwatch check = Stopwatch.StartNew();
                ulong? incomplete = reopenedStore.FindIncompleteDataColumnSlot(First, top, currentEpoch: null, required, out BeaconChainStore.DataColumnShortfall shortfall);
                check.Stop();
                Assert.That(incomplete, Is.Null, "the measured range is complete");
                laps[lap] = $"{check.Elapsed.TotalMilliseconds:F0} ms (incomplete: {incomplete?.ToString() ?? "none"}, {shortfall})";
            }

            string report = $"stored range check, {Window} slots, {sampled.Count} sampled columns, build {build.Elapsed.TotalSeconds:F0} s; after reopen: first {laps[0]}, second {laps[1]}, third {laps[2]}";
            TestContext.Out.WriteLine(report);
            Console.WriteLine(report);
            Assert.That(top, Is.EqualTo(Last));
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Test]
    public void Pruning_a_finalized_epoch_on_RocksDB_with_mainnet_sized_state_snapshots()
    {
        string path = Path.Combine(Path.GetTempPath(), "bc-state-prune-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        const int StateCount = 256;
        const int StateLength = 160 * 1024 * 1024;
        try
        {
            byte[] state = new byte[StateLength];
            using (ColumnsDb<BeaconChainDbColumns> db = Open(path))
            {
                BeaconChainStore store = new(db);
                for (ulong slot = 1; slot <= StateCount; slot++)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(state.AsSpan(40), slot);
                    store.PutState(RootAt(slot), state);
                }

                db.Flush();
            }

            using ColumnsDb<BeaconChainDbColumns> reopened = Open(path);
            BeaconChainStore reopenedStore = new(reopened);
            Stopwatch prune = Stopwatch.StartNew();
            reopenedStore.SetAnchor(RootAt(32), 32);
            prune.Stop();
            TestContext.Out.WriteLine($"Pruned 31 of {StateCount} synthetic {StateLength / (1024 * 1024)} MiB states in {prune.Elapsed.TotalMilliseconds:F0} ms after reopen");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(reopened.GetColumnDb(BeaconChainDbColumns.States).KeyExists(RootAt(1).Bytes), Is.False);
                Assert.That(reopened.GetColumnDb(BeaconChainDbColumns.States).KeyExists(RootAt(32).Bytes), Is.True);
                Assert.That(reopened.GetColumnDb(BeaconChainDbColumns.States).KeyExists(RootAt(StateCount).Bytes), Is.True);
            }
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

}
