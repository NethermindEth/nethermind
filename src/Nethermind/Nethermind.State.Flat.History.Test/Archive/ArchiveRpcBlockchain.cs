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
    // Shrinks the history write buffers and level targets so a few hundred MB of generated rows reaches the
    // same level count production reaches with hundreds of GB. Level count is what drives the number
    // of child iterators each seek builds, so the A/B ratio transfers even though the absolute numbers do not.
    private const string SmallBufferShapeOptions =
        "write_buffer_size=4000000;" +
        "max_write_buffer_number=2;" +
        "target_file_size_base=4000000;" +
        "max_bytes_for_level_base=16000000;" +
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
