// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.BeaconChain;

public interface IBeaconChainConfig : IConfig
{
    [ConfigItem(Description = "Whether to enable the embedded beacon chain consensus driver. When enabled, Nethermind follows the beacon chain for the execution layer's configured network without an external consensus client.", DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(Description = "The beacon API URL to checkpoint-sync the finalized beacon state and block from. When unset, defaults to a provider for the network selected via the execution layer's chain id.", DefaultValue = "null")]
    string? CheckpointSyncUrl { get; set; }

    [ConfigItem(Description = "A local SSZ-encoded beacon state file to bootstrap from. Requires a sibling block file with the extension replaced by .block.ssz. For a state advanced beyond its block, also requires the block's post-state in a sibling file with the extension replaced by .post-state.ssz.", DefaultValue = "null")]
    string? CheckpointStateFile { get; set; }

    [ConfigItem(Description = "An independently trusted weak subjectivity checkpoint as block_root:epoch_number, for example 0x8584188b86a9296932785cc2827b925f9deebacce6d72ad8d53171fa046b43d9:9544. Checkpoint sync fails unless its anchor block is this checkpoint's block at the start of the epoch, so supply the checkpoint's state with CheckpointStateFile or a source serving it as finalized; a proven checkpoint stays accepted for the same database, and any other fails startup. When unset, no checkpoint is required.", DefaultValue = "null")]
    string? WeakSubjectivityCheckpoint { get; set; }

    [ConfigItem(Description = "The TCP port for the beacon chain libp2p host.", DefaultValue = "9050")]
    int P2PPort { get; set; }

    [ConfigItem(Description = "The UDP port for the beacon chain discv5 discovery.", DefaultValue = "9050")]
    int Discv5Port { get; set; }

    [ConfigItem(Description = "Comma-separated beacon chain bootnode ENRs. When empty, the built-in bootnodes of the network selected via the execution layer's chain id are used.", DefaultValue = "null")]
    string? Bootnodes { get; set; }

    [ConfigItem(Description = "Comma-separated static beacon chain peer multiaddrs to keep persistent connections to. Each multiaddr must name an /ip4 or /ip6 address and include the /p2p/<peer-id> component.", DefaultValue = "null")]
    string? StaticPeers { get; set; }

    [ConfigItem(Description = "The target number of beacon chain peers.", DefaultValue = "50")]
    int TargetPeerCount { get; set; }

    [ConfigItem(Description = "The low watermark: below this many connected peers the node is under-peered, and the peer manager runs its maintenance round (static-peer reconnect, health checks) on a shorter cadence. Candidate discovery pacing is still BeaconDiscovery's own concern, not this config's.", DefaultValue = "20")]
    int MinPeerCount { get; set; }

    [ConfigItem(Description = "The high watermark. Above this many connected peers, the peer manager trims the worst peers back down to TargetPeerCount.", DefaultValue = "80")]
    int MaxPeerCount { get; set; }

    [ConfigItem(Description = "The max number of outbound dials the peer manager will have in flight at once, static and discovered peers combined.", DefaultValue = "8")]
    int MaxConcurrentOutboundDials { get; set; }

    [ConfigItem(Description = "Consecutive goodbye-Fault disconnects from the same peer id before it is added to the in-memory ban list, which then refuses every further dial or reconnection attempt for that id regardless of the address it is next seen at.", DefaultValue = "3")]
    int FaultDisconnectsBeforeBan { get; set; }

    [ConfigItem(Description = "How long, in minutes, a banned peer id is refused before it may connect again.", DefaultValue = "30")]
    int PeerBanMinutes { get; set; }

    [ConfigItem(Description = "The interval, in epochs, between persisted beacon state snapshots. Must be at least 1.", DefaultValue = "32")]
    int StateSnapshotIntervalEpochs { get; set; }

    [ConfigItem(Description = "Overrides the Gloas fork epoch of the network selected via the execution layer's chain id, for following a network whose Gloas schedule changed since this release. Must not be below the network's Fulu fork epoch. When unset, the built-in schedule is used. For a network with no built-in Gloas epoch, GloasForkVersion must be set too.", DefaultValue = "null")]
    ulong? GloasForkEpoch { get; set; }

    [ConfigItem(Description = "Overrides the Gloas fork version of the network selected via the execution layer's chain id, as 4 bytes of hex (for example 0x90000076). Must differ from every earlier fork version of that network. When unset, the built-in version is used.", DefaultValue = "null")]
    string? GloasForkVersion { get; set; }

    [ConfigItem(Description = "Whether to permanently disable the embedded driver when an external consensus client calls the engine API.", DefaultValue = "true")]
    bool DisableOnExternalCl { get; set; }
}
