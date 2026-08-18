// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Container;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Evm.State;
using Nethermind.Init.Modules;
using Nethermind.JsonRpc.Test.Modules;

namespace Nethermind.State.Flat.History.Test.Archive;

/// <summary>
/// A <see cref="TestRpcBlockchain"/> whose databases are RocksDB on a fixed path, with the flat backend and history capture on.
/// </summary>
public sealed class ArchiveRpcBlockchain: TestRpcBlockchain
{
    // Shrinks the history write buffers and level targets so the generated rows reach the same level count
    // production reaches with hundreds of GB. Level count is what drives the number of child iterators each seek
    // builds, so the A/B ratio transfers even though the absolute numbers do not.
    //
    // The targets are this small because the rows compress far harder than their raw size suggests: the keys are
    // sorted and nearly identical, so a million of them land in ~8 MB. With a level base of any ordinary size the
    // whole index fits in L1 and no deeper level ever forms. Sizing is static here
    // (level_compaction_dynamic_level_bytes is false), so the targets are exactly base * multiplier^(n-1):
    // L1 0.3 MB, L2 0.6, L3 1.2, L4 2.4, L5 4.8 — 9.3 MB of capacity through L5, which puts ~8 MB of index across
    // five levels. A multiplier of 3 reaches L4 and stops, which is not enough seek depth to measure.
    private const string SmallBufferShapeOptions =
        "write_buffer_size=1000000;" +
        "max_write_buffer_number=2;" +
        "target_file_size_base=300000;" +
        "max_bytes_for_level_base=300000;" +
        "max_bytes_for_level_multiplier=2;" +
        "level0_file_num_compaction_trigger=4;";

    private string _dbPath = null!;
    private ArchiveChainShape _shape;

    private ArchiveRpcBlockchain()
    {
        SealEngineType = Nethermind.Core.SealEngineType.NethDev;
        UseFlatDb = true;
        // Generation produces blocks of tens of millions of gas; the 30s default trips on the slower ones.
        TestTimeout = (long)TimeSpan.FromMinutes(10).TotalMilliseconds;
    }

    public static Task<ArchiveRpcBlockchain> Create(string dbPath, ArchiveChainShape shape, bool resume)
    {
        Directory.CreateDirectory(dbPath);

        ArchiveRpcBlockchain chain = new()
        {
            _dbPath = dbPath,
            _shape = shape
        };

        return ForTest(chain).Build(chain.Configure(resume));
    }

    protected override IEnumerable<IConfig> CreateConfigs() =>
    [
        .. base.CreateConfigs(),
        new InitConfig {BaseDbPath = _dbPath},
        new FlatDbConfig {Enabled = true, HistoryEnabled = true},
        new DbConfig {FlatHistoryDbAdditionalRocksDbOptions = SmallBufferShapeOptions},
    ];

    private Action<ContainerBuilder> Configure(bool resume) => builder => builder
        .AddSingleton<IDbFactory, RocksDbFactory>() // replaces MemDbFactory
        .AddColumnDatabase<FlatDbColumns>(DbNames.Flat) // PseudoNethermindModule uses in-memory DB for flat columns, use factory instead
        .ConfigureTestConfiguration(conf =>
        {
            conf.AddBlockOnStart = false;
            conf.SuggestGenesisOnStart = !resume; // On reuse the chain, including genesis, comes back from disk.
        })
        .WithGenesisPostProcessor((block, worldState, specProvider) =>
        {
            // The produced limit only drifts by ~1/1024 per block, so the room for a whole sweep has to exist from genesis.
            block.Header.GasLimit = _shape.BlockGasLimit;

            worldState.CreateAccount(StorageSweepContract.Address, 0);
            worldState.InsertCode(StorageSweepContract.Address, StorageSweepContract.RuntimeCode, specProvider.GenesisSpec);
        });
}
