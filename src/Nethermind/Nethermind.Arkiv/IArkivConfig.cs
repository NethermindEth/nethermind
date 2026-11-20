// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Arkiv;

public interface IArkivConfig : IConfig
{
    [ConfigItem(Description = "Runs Nethermind as an Arkiv database-chain execution node (Rabdi and other GolemDB networks).", DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(Description = "The network's execution genesis file, the one carrying `config.arkiv.golemdb`. Defaults to `Init.ChainSpecPath`, which is that file for the networks Nethermind ships (`chainspec/rabdi.json`). A relative path resolves against the working directory, then against Nethermind's directory.", DefaultValue = "null")]
    string? GenesisFile { get; set; }

    [ConfigItem(Description = "Directory of the GolemDB store holding Arkiv's state. Defaults to `arkiv_golemdb` under the database path.", DefaultValue = "null")]
    string? StoreDirectory { get; set; }

    [ConfigItem(Description = "Path of the native Arkiv library (libarkiv_ffi). Defaults to the one beside the plugin.", DefaultValue = "null")]
    string? NativeLibrary { get; set; }

    [ConfigItem(Description = "Keep every entity's state after every block, so `arkiv_getEntity` answers at any past block.", DefaultValue = "true")]
    bool EntityHistory { get; set; }

    [ConfigItem(Description = "Copy the GolemDB store every this many committed blocks, so a reorg below the store's head can be replayed from a copy. 0 disables copies.", DefaultValue = "0")]
    ulong CheckpointInterval { get; set; }

    [ConfigItem(Description = "Commit a block to the GolemDB store only once a block builds on it (or this node builds on it). A competing block at the same height then executes over the same parent, so a one-block reorg at the tip costs nothing; committed first, the block could only be replaced by restoring a store copy, as GolemDB has no rewind. Until then `arkiv_*` reads and `eth_call` see the block before the head.", DefaultValue = "true")]
    bool HoldHeadBlock { get; set; }

    [ConfigItem(Description = "Serve a copy of the GolemDB store to peers that state-sync (`arkiv/1`). The copy is made at the head when a peer asks and the last one is stale.", DefaultValue = "true")]
    bool ServeState { get; set; }

    [ConfigItem(Description = "The fewest blocks between two store copies served to peers that state-sync (a copy is several GB). A new one is made, at the head, when a peer needs a newer copy than the last.", DefaultValue = "64")]
    ulong ServedCopyAge { get; set; }
}

public class ArkivConfig : IArkivConfig
{
    public bool Enabled { get; set; }
    public string? GenesisFile { get; set; }
    public string? StoreDirectory { get; set; }
    public string? NativeLibrary { get; set; }
    public bool EntityHistory { get; set; } = true;
    public ulong CheckpointInterval { get; set; }
    public bool HoldHeadBlock { get; set; } = true;
    public bool ServeState { get; set; } = true;
    public ulong ServedCopyAge { get; set; } = 64;
}
