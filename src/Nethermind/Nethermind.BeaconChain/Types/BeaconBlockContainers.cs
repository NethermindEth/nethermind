// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.SszRest;
using Nethermind.Serialization.Ssz;

namespace Nethermind.BeaconChain.Types;

/// <summary>Electra <c>BeaconBlockBody</c> (unchanged in Fulu).</summary>
[SszContainer]
public partial class BeaconBlockBody
{
    public BlsSignature RandaoReveal { get; set; }

    public Eth1Data? Eth1Data { get; set; }

    public Hash256? Graffiti { get; set; }

    [SszList(16)]
    public ProposerSlashing[]? ProposerSlashings { get; set; }

    [SszList(1)]
    public AttesterSlashing[]? AttesterSlashings { get; set; }

    [SszList(8)]
    public Attestation[]? Attestations { get; set; }

    [SszList(16)]
    public Deposit[]? Deposits { get; set; }

    [SszList(16)]
    public SignedVoluntaryExit[]? VoluntaryExits { get; set; }

    public SyncAggregate? SyncAggregate { get; set; }

    public ExecutionPayload? ExecutionPayload { get; set; }

    [SszList(16)]
    public SignedBlsToExecutionChange[]? BlsToExecutionChanges { get; set; }

    [SszList(4096)]
    public SszKzgCommitment[]? BlobKzgCommitments { get; set; }

    public ExecutionRequests? ExecutionRequests { get; set; }
}

/// <summary>Electra <c>BeaconBlock</c>.</summary>
[SszContainer]
public partial class BeaconBlock
{
    public ulong Slot { get; set; }

    public ulong ProposerIndex { get; set; }

    public Hash256? ParentRoot { get; set; }

    public Hash256? StateRoot { get; set; }

    public BeaconBlockBody? Body { get; set; }
}

/// <summary>Electra <c>SignedBeaconBlock</c>.</summary>
[SszContainer]
public partial class SignedBeaconBlock
{
    public BeaconBlock? Message { get; set; }

    public BlsSignature Signature { get; set; }
}
