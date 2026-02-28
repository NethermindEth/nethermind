// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Xdc;
using Nethermind.Xdc.Contracts;
using Nethermind.Xdc.Types;
using NSubstitute;

namespace Xdc;

public static class Migrator
{
    public static MigrationResult Migrate(MigrationArguments args)
    {
        using SourceContext source = OpenSourceDatabase(args.SourceDir);
        using TargetContext target = OpenTargetDatabase(args.TargetDir, source.StateRoot);

        Console.WriteLine("Migrating state and code...");
        var copier = new XdcTrieCopier(source, target);
        source.StateTree.Accept(copier, source.StateRoot, new VisitingOptions { FullScanMemoryBudget = 0 });

        Console.WriteLine("Migrating snapshots...");
        const int snapCount = 10; // TODO: allow in parameters?
        StoreSnapshots(source, target, snapCount);

        if (args.Verify)
        {
            // TODO: add more verifications
            Console.WriteLine("Verifying resulting db...");
            VerifyNoMissingNodes(source.StateTree, target.StateTree);
            Console.WriteLine("Verification successful");
        }

        return new MigrationResult(copier.StateNodesCopied, copier.CodeNodesCopied);
    }

    private static void StoreSnapshots(SourceContext source, TargetContext target, int maxCount)
    {
        var manager = new SnapshotManager(
            target.SnapshotDb,
            Substitute.For<IBlockTree>(), Substitute.For<IMasternodeVotingContract>(), Substitute.For<ISpecProvider>()
        );

        Snapshot? snapshot = source.Reader.GetLatestSnapshot(source.Pivot.Number);
        var count = 0;
        while (snapshot is not null && count < maxCount)
        {
            manager.StoreSnapshot(snapshot);
            count++;

            snapshot = source.Reader.GetLatestSnapshot(snapshot.BlockNumber - 1);
        }

        Console.WriteLine($"Migrated {count} latest snapshots");
    }

    private static XdcBlockHeader RetractUntil(XdcBlockHeader from, XdcReader reader, string what, Func<XdcBlockHeader, bool> shouldStop)
    {
        XdcBlockHeader? header = from;
        Console.WriteLine($"Retracting from {Format(header)} to find {what} block...");

        while (header is not null)
        {
            if (shouldStop(header) || header.ParentHash is null || header.ParentHash == Keccak.Zero)
                break;

            header = reader.GetHeader(header.ParentHash);
        }

        if (header is null)
            throw new Exception($"Failed to find {what}.");

        Console.WriteLine($"Found {what} block at {Format(header)}");
        return header;
    }

    private static SourceContext OpenSourceDatabase(string dbPath)
    {
        var db = new ReadOnlyLevelDb(dbPath);
        var reader = new XdcReader(db);

        XdcBlockHeader? head = reader.GetHeadHeader(includeTD: false)
            ?? throw new Exception("Failed to get head header.");

        Console.WriteLine($"Found head block at {Format(head)}");

        XdcBlockHeader pivot = RetractUntil(head, reader, "epoch switch", header => header.Validators is {Length: > 0})
            ?? throw new Exception("Failed to find epoch switch block.");

        Console.WriteLine($"Using pivot: {Format(pivot)}");

        return new SourceContext(db, reader, pivot);
    }

    private static TargetContext OpenTargetDatabase(string dbPath, Hash256 stateRoot)
    {
        var configFactory = new RocksDbConfigFactory(DbConfig.Default, new PruningConfig {Enabled = false}, new HardwareInfo(), NullLogManager.Instance);
        var dbFactory = new RocksDbFactory(configFactory, DbConfig.Default, new HyperClockCacheWrapper(), NullLogManager.Instance, dbPath);
        return new TargetContext(dbFactory, stateRoot);
    }

    private static void VerifyNoMissingNodes(StateTree sourceTree, StateTree targetTree)
    {
        var counter = new TrieNodeCounter();

        targetTree.Accept(counter, sourceTree.RootHash, new() {FullScanMemoryBudget = 0});

        if (counter.MissingCount > 0)
            throw new Exception($"{counter.MissingCount} missing nodes found during verification.");
    }

    private static string Format(XdcBlockHeader header) => header.TotalDifficulty is null
        ? $"{header.ToString(BlockHeader.Format.FullHashAndNumber)}"
        : $"{header.ToString(BlockHeader.Format.FullHashAndNumber)} [TD: {header.TotalDifficulty}]";
}
