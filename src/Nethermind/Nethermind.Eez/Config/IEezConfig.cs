// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Eez.Config;

[ConfigCategory(Description = "EEZ rollup L2 execution rules")]
public interface IEezConfig : IConfig
{
    [ConfigItem(Description = "Whether to execute the chain under the EEZ L2 rules. Requires an EEZ genesis.", DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(Description = "Whether to follow the rollup from L1: derive every L2 block from the batches L1 settled. Requires `Eez.Enabled`. " +
        "The follower must be the only driver of the node's engine API: a consensus client that moves the forkchoice stops it.", DefaultValue = "false")]
    bool FollowerEnabled { get; set; }

    [ConfigItem(Description = "The JSON-RPC URL of the rollup's sequencer. When set, the follower also takes the sequencer's latest blocks as the " +
        "unsafe head before L1 settles them; the safe and finalized heads still follow L1 alone.", DefaultValue = "null")]
    string? SequencerRpcUrl { get; set; }

    [ConfigItem(Description = "The JSON-RPC URL of the L1 execution client the follower reads batches from.", DefaultValue = "null")]
    string? L1RpcUrl { get; set; }

    [ConfigItem(Description = "The chain ID the L1 execution client must report.", DefaultValue = "0")]
    ulong L1ChainId { get; set; }

    [ConfigItem(Description = "The address of the EEZ registry contract on L1.", DefaultValue = "null")]
    string? RegistryAddress { get; set; }

    [ConfigItem(Description = "The L1 block the EEZ registry was deployed in; the follower scans L1 from it.", DefaultValue = "0")]
    ulong RegistryDeployBlock { get; set; }

    [ConfigItem(Description = "The rollup ID of this L2 in the EEZ registry.", DefaultValue = "0")]
    ulong RollupId { get; set; }

    [ConfigItem(Description = "The L2 block time, in whole seconds: derivation adds it to each parent's timestamp.", DefaultValue = "2")]
    ulong L2BlockTimeSeconds { get; set; }

    [ConfigItem(Description = "The gas limit derivation gives every L2 block.", DefaultValue = "30000000")]
    ulong L2GasLimit { get; set; }

    [ConfigItem(Description = "How often the follower polls L1 for new blocks, in milliseconds.", DefaultValue = "2000")]
    int L1PollingIntervalMs { get; set; }

    [ConfigItem(Description = "The most L1 blocks the follower reads logs for in one request.", DefaultValue = "100000")]
    ulong L1LogScanBlocks { get; set; }
}
