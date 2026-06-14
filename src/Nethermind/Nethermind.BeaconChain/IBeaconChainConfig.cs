// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.BeaconChain;

public interface IBeaconChainConfig : IConfig
{
    [ConfigItem(Description = "Whether to enable the embedded beacon chain consensus driver. When enabled, Nethermind follows Ethereum mainnet without an external consensus client.", DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(Description = "The beacon API URL to checkpoint-sync the finalized beacon state and block from.", DefaultValue = "https://mainnet.checkpoint.sigp.io")]
    string CheckpointSyncUrl { get; set; }

    [ConfigItem(Description = "A local SSZ-encoded beacon state file to bootstrap from instead of downloading from the checkpoint sync URL.", DefaultValue = "null")]
    string? CheckpointStateFile { get; set; }

    [ConfigItem(Description = "The TCP port for the beacon chain libp2p host.", DefaultValue = "9050")]
    int P2PPort { get; set; }

    [ConfigItem(Description = "The UDP port for the beacon chain discv5 discovery.", DefaultValue = "9050")]
    int Discv5Port { get; set; }

    [ConfigItem(Description = "Comma-separated beacon chain bootnode ENRs. When empty, the built-in mainnet bootnodes are used.", DefaultValue = "null")]
    string? Bootnodes { get; set; }

    [ConfigItem(Description = "Comma-separated static beacon chain peer multiaddrs to keep persistent connections to. Each multiaddr must include the /p2p/<peer-id> component.", DefaultValue = "null")]
    string? StaticPeers { get; set; }

    [ConfigItem(Description = "The target number of beacon chain peers.", DefaultValue = "50")]
    int TargetPeerCount { get; set; }

    [ConfigItem(Description = "The max number of beacon chain peers.", DefaultValue = "80")]
    int MaxPeerCount { get; set; }

    [ConfigItem(Description = "The interval, in epochs, between persisted beacon state snapshots.", DefaultValue = "32")]
    int StateSnapshotIntervalEpochs { get; set; }

    [ConfigItem(Description = "Whether to permanently disable the embedded driver when an external consensus client calls the engine API.", DefaultValue = "true")]
    bool DisableOnExternalCl { get; set; }
}
