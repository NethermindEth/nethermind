// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Logging;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using WholeReadScanner = Nethermind.State.Flat.PersistedSnapshots.PersistedSnapshotScanner<
    Nethermind.State.Flat.PersistedSnapshots.Storage.WholeReadSession,
    Nethermind.State.Flat.PersistedSnapshots.Storage.WholeReadSessionReader,
    Nethermind.State.Flat.Io.NoOpPin>;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>
/// The converted snapshots a persisted-snapshot tier left on disk, read back through the C# tier: per catalogued base
/// snapshot its range, its records with every node reference resolved to its RLP, and whether the reopened repository
/// holds its state.
/// </summary>
/// <remarks>
/// The merged snapshots are left out, and so is where a snapshot lies, in which file and at which offset, and the blob
/// range and blob references of its table: which merges ran on their windows, and in what order, depends on when the
/// persisted-snapshot compactor of either backend ran relative to the prunes, so that differs even between two runs of
/// the C# backend. Taken only once the manager that wrote the tier is disposed, from the catalog RocksDB in the base
/// path (<see cref="FlatTestDbs.OpenRocksDbCatalog"/>).
/// </remarks>
internal static class PersistedDump
{
    public static List<string> Take(string basePath, FlatDbConfig config)
    {
        using DbOnTheRocks catalogDb = FlatTestDbs.OpenRocksDbCatalog(basePath);
        string tierPath = Path.Combine(basePath, "persistedSnapshot");
        using BlobArenaManager blobs = new(Path.Combine(tierPath, "blob"), config.ArenaFileSizeBytes);
        using ArenaManager arena = new(Path.Combine(tierPath, "arena"), config, LimboLogs.Instance);
        SnapshotCatalog catalog = new(catalogDb);
        using SnapshotRepository repository = new(arena, blobs, catalog, config, UnavailableStateHeaderProvider.Instance, LimboLogs.Instance);
        // Disposed first, so the files of the snapshots it loaded are kept.
        using PersistedSnapshotLoader loader = new(repository, arena, blobs, catalog, config, LimboLogs.Instance);
        loader.Load();

        List<string> dump = [];
        CatalogEntry[] entries = [.. catalog.Load()
            .Where(static entry => entry.Tier == SnapshotTier.PersistedBase)
            .OrderBy(static entry => entry.To.BlockNumber).ThenBy(static entry => entry.To.StateRoot).ThenBy(static entry => entry.From.BlockNumber)];
        foreach (CatalogEntry entry in entries)
        {
            string snapshot = $"{entry.From.BlockNumber}:{entry.From.StateRoot}->{entry.To.BlockNumber}:{entry.To.StateRoot}";
            dump.Add($"{snapshot} held {repository.HasState(entry.To)}");
            if (!repository.TryLeasePersistedState(entry.To, entry.Tier, out PersistedSnapshot? persisted))
            {
                dump.Add($"{snapshot} not loaded");
                continue;
            }

            using (persisted)
            {
                dump.AddRange(Records(persisted).Select(record => $"{snapshot} {record}"));
            }
        }

        return dump;
    }

    private static List<string> Records(PersistedSnapshot snapshot)
    {
        List<string> records = [];
        using WholeReadSession session = snapshot.BeginWholeReadSession(adviseDontNeedOnDispose: true);
        WholeReadScanner scanner = PersistedSnapshotScanner.ForWholeRead(session, snapshot);
        foreach (WholeReadScanner.PerAddressEntry entry in scanner.PerAddresses)
        {
            records.Add($"account {entry.Address} {(entry.HasAccount ? Render(entry.Account) : "unchanged")} self-destruct {entry.SelfDestructFlag?.ToString() ?? "none"}");
            foreach (WholeReadScanner.SlotEntry slot in entry.Slots)
            {
                records.Add($"slot {entry.Address} {slot.Slot} {slot.Value?.ToString() ?? "null"}");
            }
        }

        foreach (WholeReadScanner.StateNodeEntry node in scanner.StateNodes)
        {
            records.Add($"state node {node.Path} {node.Rlp.ToHexString()}");
        }

        foreach (WholeReadScanner.StorageNodeEntry node in scanner.StorageNodes)
        {
            records.Add($"storage node {node.AddressHash} {node.Path} {node.Rlp.ToHexString()}");
        }

        return records;
    }

    private static string Render(Account? account) =>
        account is null ? "deleted" : $"{account.Nonce} {account.Balance} {account.StorageRoot} {account.CodeHash}";
}
