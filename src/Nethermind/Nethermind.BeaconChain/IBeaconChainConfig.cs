// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.BeaconChain;

public interface IBeaconChainConfig : IConfig
{
    [ConfigItem(Description = "Whether to enable the embedded beacon chain consensus driver. Supports Mainnet, Sepolia, Hoodi, Gnosis and Chiado, selected by the execution layer's configured network, without an external consensus client.", DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(Description = "Use the Fast Confirmation Rule for the execution layer's safe block tag, as required by consensus-specs v1.7.0-beta.4. Assumes slot synchrony and at most 25% Byzantine stake; falls back to finalized when confirmation conditions fail. Enabled by default; when disabled, safe follows the justified checkpoint. Does not change finalized.", DefaultValue = "true")]
    bool EnableFastConfirmation { get; set; }

    [ConfigItem(Description = "The beacon API URL to checkpoint-sync the finalized beacon state and block from. When unset, defaults to a provider for the network selected via the execution layer's chain id.", DefaultValue = "null")]
    string? CheckpointSyncUrl { get; set; }

    [ConfigItem(Description = "A local SSZ-encoded beacon state file to bootstrap from. Requires a sibling block file with the extension replaced by .block.ssz. For a state advanced beyond its block, also requires the block's post-state in a sibling file with the extension replaced by .post-state.ssz.", DefaultValue = "null")]
    string? CheckpointStateFile { get; set; }

    [ConfigItem(Description = "An independently trusted weak subjectivity checkpoint as block_root:epoch_number, for example 0x8584188b86a9296932785cc2827b925f9deebacce6d72ad8d53171fa046b43d9:9544. Checkpoint sync fails unless its anchor block is this checkpoint's block at the start of the epoch, so supply the checkpoint's state with CheckpointStateFile or a source serving it as finalized; a proven checkpoint stays accepted for the same database, and any other fails startup. When unset, checkpoint sync trusts the configured checkpoint source, or the default provider for the network when CheckpointSyncUrl is also unset.", DefaultValue = "null")]
    string? WeakSubjectivityCheckpoint { get; set; }

    [ConfigItem(Description = "The TCP port for the beacon chain libp2p host.", DefaultValue = "9060")]
    int P2PPort { get; set; }

    [ConfigItem(Description = "The UDP port for the beacon chain discv5 discovery.", DefaultValue = "9060")]
    int Discv5Port { get; set; }

    [ConfigItem(Description = "Number of PeerDAS custody groups, from 4 to 128. Use at least 64 to reconstruct blobs for the Beacon API, or 128 to serve all columns. Default: 4 groups.", DefaultValue = "4")]
    ulong CustodyGroupCount { get; set; }

    [ConfigItem(Description = "Comma-separated beacon chain bootnode ENRs. When empty, the built-in bootnodes of the network selected via the execution layer's chain id are used.", DefaultValue = "null")]
    string? Bootnodes { get; set; }

    [ConfigItem(Description = "Comma-separated static beacon chain peer multiaddrs to keep persistent connections to. Each multiaddr must name an /ip4 or /ip6 address, or a /dns, /dns4 or /dns6 TCP endpoint, and include the /p2p/<peer-id> component.", DefaultValue = "null")]
    string? StaticPeers { get; set; }

    [ConfigItem(Description = "The target number of beacon chain peers.", DefaultValue = "50")]
    int TargetPeerCount { get; set; }

    [ConfigItem(Description = "The most gossip messages that may await a validation verdict at once. A message received past it is not validated, forwarded or charged to its sender, so a later copy can still be. Must be at least 1.", DefaultValue = "128")]
    int GossipMaxPendingValidations { get; set; }

    [ConfigItem(Description = "The most bytes of gossip messages that may await a validation verdict at once, the messages of one received RPC aside. A message received past it is not validated, forwarded or charged to its sender. Must be at least 1.", DefaultValue = "16777216")]
    int GossipMaxPendingValidationBytes { get; set; }

    [ConfigItem(Description = "The low watermark: below this many connected peers the node is under-peered, and the peer manager runs its maintenance round (static-peer reconnect, health checks) on a shorter cadence. Candidate discovery pacing is still BeaconDiscovery's own concern, not this config's.", DefaultValue = "20")]
    int MinPeerCount { get; set; }

    [ConfigItem(Description = "The high watermark. Above this many connected peers, the peer manager trims the worst peers back down to this many.", DefaultValue = "80")]
    int MaxPeerCount { get; set; }

    [ConfigItem(Description = "The max number of outbound dials the peer manager will have in flight at once, static and discovered peers combined.", DefaultValue = "8")]
    int MaxConcurrentOutboundDials { get; set; }

    [ConfigItem(Description = "Recent fault disconnects from the same peer id before it is added to the in-memory ban list, which then refuses every further dial or reconnection attempt for that id regardless of the address it is next seen at.", DefaultValue = "3")]
    int FaultDisconnectsBeforeBan { get; set; }

    [ConfigItem(Description = "How long, in minutes, a banned peer id is refused before it may connect again. Fault history expires after this duration without another fault.", DefaultValue = "30")]
    int PeerBanMinutes { get; set; }

    [ConfigItem(Description = "The Gloas fork version, as 4 bytes of hex (for example 0x90000076), for a network whose chainspec schedules Amsterdam before this release knows its published Gloas version. The Gloas epoch always follows the Amsterdam timestamp of the chainspec (see Init.ChainSpecPath). Must differ from every earlier fork version of the network. When unset, the published version is used.", DefaultValue = "null")]
    string? GloasForkVersion { get; set; }

    [ConfigItem(Description = "A successful external engine progression call disables the embedded driver until process restart, including a transient caller. Whether to permanently disable the embedded driver when an external consensus client calls the engine API.", DefaultValue = "true")]
    bool DisableOnExternalCl { get; set; }
}
