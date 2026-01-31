// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Xdc;

namespace Xdc;

public static class Migrator
{
    public static MigrationResult Migrate(MigrationArguments args)
    {
        using SourceContext source = OpenSourceDatabase(args.SourceDir);
        using TargetContext target = OpenTargetDatabase(args.TargetDir, source.StateRoot);

        var copier = new XdcTrieCopier(source, target);
        source.StateTree.Accept(copier, source.StateRoot, new VisitingOptions { FullScanMemoryBudget = 0 });

        if (args.Verify)
        {
            // TODO: add more verifications
            VerifyNoMissingNodes(source.StateTree, target.StateTree);

            Console.WriteLine("Verification successful.");
        }

        return new MigrationResult(copier.StateNodesCopied, copier.CodeNodesCopied);
    }

    private static SourceContext OpenSourceDatabase(string dbPath)
    {
        var db = new ReadOnlyLevelDb(dbPath);

        byte[] lastHeaderHash = db.Get(XdcSchema.HeadHeaderKey)
            ?? throw new Exception("Failed to find latest header hash.");;

        byte[] headerNumberKey = [.. XdcSchema.HeaderNumberPrefix, .. lastHeaderHash];
        byte[] blockNumberBytes = db.Get(headerNumberKey)
            ?? throw new Exception("Failed to find block number from the last header hash.");

        byte[] headerKey = [.. XdcSchema.HeaderPrefix, .. blockNumberBytes, .. lastHeaderHash];
        byte[] headerRlp = db.Get(headerKey)
            ?? throw new Exception("Failed to find header RLP.");

        BlockHeader header = new XdcHeaderDecoder().Decode(headerRlp);
        Hash256 stateRoot = header.StateRoot
            ?? throw new Exception("Failed to find state root from the header.");

        return new SourceContext(db, stateRoot);
    }

    private static TargetContext OpenTargetDatabase(string dbPath, Hash256 stateRoot)
    {
        var configFactory = new RocksDbConfigFactory(DbConfig.Default, new PruningConfig {Enabled = false}, new HardwareInfo(), NullLogManager.Instance);
        var dbFactory = new RocksDbFactory(configFactory, DbConfig.Default, new HyperClockCacheWrapper(), NullLogManager.Instance, dbPath);

        IDb stateDb = dbFactory.CreateDb(new DbSettings(DbNames.State, "state"));
        IDb codeDb = dbFactory.CreateDb(new DbSettings(DbNames.State, "code"));

        return new TargetContext(stateDb, codeDb, stateRoot);
    }

    private static void VerifyNoMissingNodes(StateTree sourceTree, StateTree targetTree)
    {
        var counter = new TrieNodeCounter();

        targetTree.Accept(counter, sourceTree.RootHash, new() {FullScanMemoryBudget = 0});

        if (counter.MissingCount > 0)
            throw new Exception($"{counter.MissingCount} missing nodes found during verification.");
    }
}
