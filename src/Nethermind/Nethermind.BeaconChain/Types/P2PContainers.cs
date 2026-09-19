// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Ssz;

namespace Nethermind.BeaconChain.Types;

/// <summary>Phase0 req/resp <c>Status</c> (v1).</summary>
[SszContainer]
public partial class StatusMessageV1
{
    [SszVector(4)]
    public byte[]? ForkDigest { get; set; }

    public Hash256? FinalizedRoot { get; set; }

    public ulong FinalizedEpoch { get; set; }

    public Hash256? HeadRoot { get; set; }

    public ulong HeadSlot { get; set; }
}

/// <summary>Fulu req/resp <c>StatusV2</c>: v1 plus <c>earliest_available_slot</c> (EIP-7594 data availability advertising).</summary>
[SszContainer]
public partial class StatusMessageV2
{
    [SszVector(4)]
    public byte[]? ForkDigest { get; set; }

    public Hash256? FinalizedRoot { get; set; }

    public ulong FinalizedEpoch { get; set; }

    public Hash256? HeadRoot { get; set; }

    public ulong HeadSlot { get; set; }

    public ulong EarliestAvailableSlot { get; set; }
}

/// <summary>Fulu req/resp <c>MetaData</c> (v3): altair fields plus <c>custody_group_count</c>.</summary>
[SszContainer]
public partial class MetaDataV3
{
    public ulong SeqNumber { get; set; }

    [SszVector(64)]
    public BitArray? Attnets { get; set; }

    [SszVector(4)]
    public BitArray? Syncnets { get; set; }

    public ulong CustodyGroupCount { get; set; }
}

/// <summary>Phase0 req/resp <c>BeaconBlocksByRangeRequest</c>.</summary>
[SszContainer]
public partial class BeaconBlocksByRangeRequest
{
    public ulong StartSlot { get; set; }

    public ulong Count { get; set; }

    /// <summary>Deprecated; clients MUST respond as if it were 1.</summary>
    public ulong Step { get; set; }
}

/// <summary>Deneb req/resp <c>BeaconBlocksByRootRequest</c>: a bare SSZ list of block roots.</summary>
[SszContainer(isCollectionItself: true)]
public partial class BeaconBlocksByRootRequest
{
    [SszList(128)]
    public Hash256[]? Roots { get; set; }
}
