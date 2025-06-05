// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Optimism.CL.Derivation;
using Nethermind.Optimism.Rpc;

namespace Nethermind.Optimism.CL;

public class L2Block
{
    public required Hash256 Hash { get; init; }
    public required Hash256 ParentHash { get; init; }
    public required Hash256 StateRoot { get; init; }
    public required PayloadAttributesRef PayloadAttributesRef { get; init; }
    public ulong Number => PayloadAttributesRef.Number;
    public OptimismPayloadAttributes PayloadAttributes => PayloadAttributesRef.PayloadAttributes;
    public SystemConfig SystemConfig => PayloadAttributesRef.SystemConfig;
    public L1BlockInfo L1BlockInfo => PayloadAttributesRef.L1BlockInfo;
}

/// <remarks>
/// Spec: https://specs.optimism.io/protocol/rollup-node.html?utm_source=op-docs&utm_medium=docs#l2blockref
/// </remarks>
public sealed record L2BlockRef
{
    public required Hash256 Hash { get; init; }
    public required ulong Number { get; init; }
    public required Hash256 ParentHash { get; init; }
    public required ulong Timestamp { get; init; }
    public required BlockId L1Origin { get; init; }
    public required ulong SequenceNumber { get; init; }

    public static L2BlockRef Zero => new()
    {
        Hash = Hash256.Zero,
        Number = 0,
        ParentHash = Hash256.Zero,
        Timestamp = 0,
        L1Origin = BlockId.Zero,
        SequenceNumber = 0
    };

    public static L2BlockRef From(L2Block? block)
    {
        return block is null ? Zero : new L2BlockRef
        {
            Hash = block.Hash,
            Number = block.Number,
            ParentHash = block.ParentHash,
            Timestamp = block.PayloadAttributes.Timestamp,
            L1Origin = BlockId.FromL1BlockInfo(block.L1BlockInfo),
            SequenceNumber = block.L1BlockInfo.SequenceNumber
        };
    }
}
