// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle.Json;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

internal sealed partial class NativeL1Rules
{
    private static JsonObject Forks(GethGenesisConfigJson config, SortedSet<ulong> times, out ulong lastBlockFork)
    {
        JsonObject forks = [];
        lastBlockFork = 0;
        foreach ((string name, ulong? at) in BlockForks(config))
        {
            if (at is not { } block) continue;
            forks[name] = new JsonObject { ["block"] = block };
            lastBlockFork = Math.Max(lastBlockFork, block);
        }
        if (config.TerminalTotalDifficulty is { } difficulty)
        {
            ulong activation = MergeActivation(config, in difficulty);
            forks["Paris"] = new JsonObject
            {
                ["activationBlock"] = activation,
                ["advertisedForkBlock"] = config.MergeNetsplitBlock,
                ["totalDifficulty"] = "0x" + difficulty.ToString("x", CultureInfo.InvariantCulture)
            };
            lastBlockFork = Math.Max(lastBlockFork, activation);
        }
        foreach ((string name, ulong? at) in TimestampForks(config))
        {
            if (at is not { } timestamp) continue;
            forks[name] = new JsonObject { ["timestamp"] = timestamp };
            times.Add(timestamp);
        }
        return forks;
    }

    private static ulong MergeActivation(GethGenesisConfigJson config, in UInt256 difficulty)
    {
        if (config.MergeNetsplitBlock is { } block) return block;
        if (config.ChainId == BlockchainIds.Mainnet && difficulty == MainnetSpecProvider.Instance.TerminalTotalDifficulty)
            return MainnetSpecProvider.ParisBlockNumber + 1;
        // Sepolia's first PoS block differs from its advertised merge fork-id block.
        if (config.ChainId == BlockchainIds.Sepolia && difficulty == SepoliaSpecProvider.Instance.TerminalTotalDifficulty)
            return 1_450_409;
        return 0;
    }

    private static (string Name, ulong? At)[] BlockForks(GethGenesisConfigJson c) =>
    [
        ("Frontier", 0), ("Homestead", c.HomesteadBlock), ("Dao", c.DaoForkBlock), ("Tangerine", c.Eip150Block),
        ("SpuriousDragon", c.Eip155Block), ("Byzantium", c.ByzantiumBlock), ("Constantinople", c.ConstantinopleBlock),
        ("Petersburg", c.PetersburgBlock), ("Istanbul", c.IstanbulBlock), ("MuirGlacier", c.MuirGlacierBlock),
        ("Berlin", c.BerlinBlock), ("London", c.LondonBlock), ("ArrowGlacier", c.ArrowGlacierBlock), ("GrayGlacier", c.GrayGlacierBlock)
    ];

    private static (string Name, ulong? At)[] TimestampForks(GethGenesisConfigJson c) =>
    [
        ("Shanghai", c.ShanghaiTime), ("Cancun", c.CancunTime), ("Prague", c.PragueTime), ("Osaka", c.OsakaTime),
        ("Bpo1", c.Bpo1Time), ("Bpo2", c.Bpo2Time), ("Bpo3", c.Bpo3Time), ("Bpo4", c.Bpo4Time), ("Bpo5", c.Bpo5Time),
        ("Amsterdam", c.AmsterdamTime), ("Bogota", c.BogotaTime)
    ];
}
