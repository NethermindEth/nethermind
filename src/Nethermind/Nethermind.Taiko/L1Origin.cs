// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Taiko;

public class L1Origin(UInt256 blockId,
    ValueHash256? l2BlockHash,
    long? l1BlockHeight,
    ValueHash256 l1BlockHash,
    int[]? buildPayloadArgsId,
    bool isForcedInclusion = false,
    int[]? signature = null)
{
    public UInt256 BlockId { get; set; } = blockId;
    public ValueHash256? L2BlockHash { get; set; } = l2BlockHash;
    public long? L1BlockHeight { get; set; } = l1BlockHeight;
    public ValueHash256 L1BlockHash { get; set; } = l1BlockHash;

    // Taiko uses int-like serializer (Go's default encoding for byte arrays)
    public int[]? BuildPayloadArgsId { get; set; } = buildPayloadArgsId;
    public bool IsForcedInclusion { get; set; } = isForcedInclusion;
    public int[]? Signature { get; set; } = signature;

    /// <summary>
    /// IsPreconfBlock returns true if the L1Origin is for a preconfirmation block.
    /// </summary>
    public bool IsPreconfBlock => L1BlockHeight == 0 || L1BlockHeight == null;
}
