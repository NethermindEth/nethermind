// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.BeaconChain;

public class BeaconChainConfig : IBeaconChainConfig
{
    public bool Enabled { get; set; }
    public string? CheckpointSyncUrl { get; set; }
    public string? CheckpointStateFile { get; set; }
    public string? WeakSubjectivityCheckpoint { get; set; }
    public int P2PPort { get; set; } = 9050;
    public int Discv5Port { get; set; } = 9050;
    public string? Bootnodes { get; set; }
    public string? StaticPeers { get; set; }
    public int TargetPeerCount { get; set; } = 50;
    public int GossipMaxPendingValidations { get; set; } = 128;
    public int GossipMaxPendingValidationBytes { get; set; } = 16 * 1024 * 1024;
    public int MinPeerCount { get; set; } = 20;
    public int MaxPeerCount { get; set; } = 80;
    public int MaxConcurrentOutboundDials { get; set; } = 8;
    public int FaultDisconnectsBeforeBan { get; set; } = 3;
    public int PeerBanMinutes { get; set; } = 30;
    public int StateSnapshotIntervalEpochs { get; set; } = 32;
    public ulong? GloasForkEpoch { get; set; }
    public string? GloasForkVersion { get; set; }
    public bool DisableOnExternalCl { get; set; } = true;
}
