// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Ssz;

namespace Nethermind.BeaconChain.Types;

/// <summary>The Deneb execution header and its proof within a beacon block.</summary>
[SszContainer]
public partial class LightClientHeader
{
    public BeaconBlockHeader? Beacon { get; set; }
    public ExecutionPayloadHeader? Execution { get; set; }
    [SszVector(4)]
    public Hash256[]? ExecutionBranch { get; set; }

    /// <summary>The authenticated execution block hash in either wire format.</summary>
    [SszIgnore]
    public Hash256? ExecutionBlockHash { get; set; }

    /// <summary>Whether this header was decoded from the Gloas wire format.</summary>
    [SszIgnore]
    public bool IsGloas { get; set; }
}

/// <summary>An Electra or Fulu light client bootstrap.</summary>
[SszContainer]
public partial class LightClientBootstrap
{
    public LightClientHeader? Header { get; set; }
    public SyncCommittee? CurrentSyncCommittee { get; set; }
    [SszVector(6)]
    public Hash256[]? CurrentSyncCommitteeBranch { get; set; }
}

/// <summary>An Electra or Fulu light client update.</summary>
[SszContainer]
public partial class LightClientUpdate
{
    public LightClientHeader? AttestedHeader { get; set; }
    public SyncCommittee? NextSyncCommittee { get; set; }
    [SszVector(6)]
    public Hash256[]? NextSyncCommitteeBranch { get; set; }
    public LightClientHeader? FinalizedHeader { get; set; }
    [SszVector(7)]
    public Hash256[]? FinalityBranch { get; set; }
    public SyncAggregate? SyncAggregate { get; set; }
    public ulong SignatureSlot { get; set; }
}

/// <summary>An Electra or Fulu finalized-header update.</summary>
[SszContainer]
public partial class LightClientFinalityUpdate
{
    public LightClientHeader? AttestedHeader { get; set; }
    public LightClientHeader? FinalizedHeader { get; set; }
    [SszVector(7)]
    public Hash256[]? FinalityBranch { get; set; }
    public SyncAggregate? SyncAggregate { get; set; }
    public ulong SignatureSlot { get; set; }
}

/// <summary>An Electra or Fulu optimistic-header update.</summary>
[SszContainer]
public partial class LightClientOptimisticUpdate
{
    public LightClientHeader? AttestedHeader { get; set; }
    public SyncAggregate? SyncAggregate { get; set; }
    public ulong SignatureSlot { get; set; }
}

/// <summary>A Gloas light-client header whose execution hash is proved in the beacon body.</summary>
[SszContainer]
public partial class GloasLightClientHeader
{
    public BeaconBlockHeader? Beacon { get; set; }
    public Hash256? ExecutionBlockHash { get; set; }
    [SszVector(11)]
    public Hash256[]? ExecutionBranch { get; set; }
}

/// <summary>A Gloas light-client bootstrap.</summary>
[SszContainer]
public partial class GloasLightClientBootstrap
{
    public GloasLightClientHeader? Header { get; set; }
    public SyncCommittee? CurrentSyncCommittee { get; set; }
    [SszVector(11)]
    public Hash256[]? CurrentSyncCommitteeBranch { get; set; }
}

/// <summary>A Gloas light-client update.</summary>
[SszContainer]
public partial class GloasLightClientUpdate
{
    public GloasLightClientHeader? AttestedHeader { get; set; }
    public SyncCommittee? NextSyncCommittee { get; set; }
    [SszVector(11)]
    public Hash256[]? NextSyncCommitteeBranch { get; set; }
    public GloasLightClientHeader? FinalizedHeader { get; set; }
    [SszVector(9)]
    public Hash256[]? FinalityBranch { get; set; }
    public SyncAggregate? SyncAggregate { get; set; }
    public ulong SignatureSlot { get; set; }
}

/// <summary>A Gloas finalized-header update.</summary>
[SszContainer]
public partial class GloasLightClientFinalityUpdate
{
    public GloasLightClientHeader? AttestedHeader { get; set; }
    public GloasLightClientHeader? FinalizedHeader { get; set; }
    [SszVector(9)]
    public Hash256[]? FinalityBranch { get; set; }
    public SyncAggregate? SyncAggregate { get; set; }
    public ulong SignatureSlot { get; set; }
}

/// <summary>A Gloas optimistic-header update.</summary>
[SszContainer]
public partial class GloasLightClientOptimisticUpdate
{
    public GloasLightClientHeader? AttestedHeader { get; set; }
    public SyncAggregate? SyncAggregate { get; set; }
    public ulong SignatureSlot { get; set; }
}
